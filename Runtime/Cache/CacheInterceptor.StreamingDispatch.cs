using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    public sealed partial class CacheInterceptor
    {
        private async ValueTask InvalidateAfterUnsafeResponseAsync(
            Uri requestUri,
            HttpHeaders responseHeaders,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var invalidationTargets = CollectInvalidationTargets(requestUri, responseHeaders ?? HttpHeaders.Empty);
            for (int i = 0; i < invalidationTargets.Count; i++)
                await InvalidateUriAsync(invalidationTargets[i], cancellationToken).ConfigureAwait(false);

            context.RecordEvent("CacheInvalidated", new Dictionary<string, object>
            {
                { "uri", NormalizeUri(requestUri) },
                { "count", invalidationTargets.Count }
            });
        }

        private async ValueTask CompleteNotModifiedRevalidationAsync(
            IHttpHandler handler,
            UHttpRequest request,
            RequestContext context,
            HttpHeaders notModifiedHeaders,
            IResponseBodySource body,
            string baseKey,
            CacheLookupResult lookup,
            CancellationToken cancellationToken)
        {
            // A 304 has no content (RFC 9110 section 15.4.5): release the transport body before touching storage.
            if (body != null)
                await body.DisposeAsync().ConfigureAwait(false);

            context.RecordEvent("CacheRevalidateNotModified");

            // RFC 9111 section 4.3.4: freshen the stored response with the 304's header fields.
            var merged = MergeNotModifiedEntry(request, lookup.Entry, notModifiedHeaders ?? HttpHeaders.Empty, baseKey);
            if (merged.Entry != null)
            {
                if (!string.Equals(lookup.StorageKey, merged.Entry.Key, StringComparison.Ordinal))
                    await ReplaceStoredEntryAsync(baseKey, merged, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                else
                    await StorePreparedEntryQueuedAsync(baseKey, merged, cancellationToken).ConfigureAwait(false);

                await ServeCachedEntryAsync(handler, merged.Entry, request, context, "REVALIDATED").ConfigureAwait(false);
                return;
            }

            await RemoveStoredEntryAsync(baseKey, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
            await ServeCachedEntryAsync(handler, lookup.Entry, request, context, "REVALIDATED").ConfigureAwait(false);
        }

        private async ValueTask CompleteModifiedRevalidationAsync(
            IHttpHandler handler,
            UHttpRequest request,
            RequestContext context,
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            string baseKey,
            CacheLookupResult lookup,
            CancellationToken cancellationToken)
        {
            context.RecordEvent("CacheRevalidateModified");

            try
            {
                // RFC 9111 section 4.3.3: the new response is authoritative and the stored one is no longer
                // valid. The replacement (when storable) is stored by CacheStoringHandler after its body
                // completes; storage is keyed by baseKey, so the queued removal runs before that store.
                await RemoveStoredEntryAsync(baseKey, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                body?.Abort();
                throw;
            }

            var storingHandler = new CacheStoringHandler(handler, this, request, baseKey, context);
            storingHandler.OnRequestStart(request, context);
            await storingHandler.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
        }

        /// <summary>
        /// Pass-through handler for unsafe methods (RFC 9111 section 4.4): when the response status is
        /// non-error (2xx/3xx) it invalidates the target URI and same-origin Location/Content-Location before
        /// the response reaches the caller, then forwards the body untouched (no buffering).
        /// </summary>
        private sealed class UnsafeInvalidationHandler : IHttpHandler
        {
            private readonly CacheInterceptor _owner;
            private readonly IHttpHandler _inner;
            private readonly Uri _requestUri;
            private readonly CancellationToken _cancellationToken;

            internal UnsafeInvalidationHandler(
                CacheInterceptor owner,
                IHttpHandler inner,
                Uri requestUri,
                CancellationToken cancellationToken)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
                _cancellationToken = cancellationToken;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (statusCode >= 200 && statusCode < 400)
                {
                    try
                    {
                        await _owner.InvalidateAfterUnsafeResponseAsync(_requestUri, headers, context, _cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        body?.Abort();
                        throw;
                    }
                }

                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }
        }

        /// <summary>
        /// Receives the conditional (validation) response. 304: the empty body is released and the merged
        /// stored entry is served. Any other status: the stored entry is removed and the response streams to
        /// the caller through <see cref="CacheStoringHandler"/>, which tees it into the store when storable.
        /// The caller's OnRequestStart is deferred to that point because a 304 synthesizes its own lifecycle.
        /// Callbacks for one dispatch are sequential; the flags only order OnResponseError against them.
        /// </summary>
        private sealed class RevalidationHandler : IHttpHandler
        {
            private readonly CacheInterceptor _owner;
            private readonly IHttpHandler _inner;
            private readonly UHttpRequest _request;
            private readonly string _baseKey;
            private readonly CacheLookupResult _lookup;
            private readonly CancellationToken _cancellationToken;
            private int _transportRequestStarted;
            private int _innerRequestStarted;

            internal RevalidationHandler(
                CacheInterceptor owner,
                IHttpHandler inner,
                UHttpRequest request,
                string baseKey,
                CacheLookupResult lookup,
                CancellationToken cancellationToken)
            {
                _owner = owner ?? throw new ArgumentNullException(nameof(owner));
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _request = request ?? throw new ArgumentNullException(nameof(request));
                _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
                _lookup = lookup;
                _cancellationToken = cancellationToken;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                Volatile.Write(ref _transportRequestStarted, 1);
            }

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                // Both branches start the caller's lifecycle with the original (unconditional) request.
                Interlocked.Exchange(ref _innerRequestStarted, 1);
                context.UpdateRequest(_request);

                if (statusCode == 304)
                {
                    return _owner.CompleteNotModifiedRevalidationAsync(
                        _inner,
                        _request,
                        context,
                        headers,
                        body,
                        _baseKey,
                        _lookup,
                        _cancellationToken);
                }

                return _owner.CompleteModifiedRevalidationAsync(
                    _inner,
                    _request,
                    context,
                    statusCode,
                    headers,
                    body,
                    _baseKey,
                    _lookup,
                    _cancellationToken);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                context.UpdateRequest(_request);
                if (Volatile.Read(ref _transportRequestStarted) != 0 &&
                    Interlocked.Exchange(ref _innerRequestStarted, 1) == 0)
                {
                    _inner.OnRequestStart(_request, context);
                }

                _inner.OnResponseError(error, context);
            }
        }

        /// <summary>
        /// Consumer for background (stale-while-revalidate) revalidation: reads a streamed replacement to its
        /// natural end so the tee stores it, bounded by the cacheable body size; anything larger is aborted.
        /// </summary>
        private sealed class BackgroundRevalidationSink : IHttpHandler
        {
            private const int ReadBufferBytes = 16 * 1024;

            private readonly long _maxBodyBytes;
            private readonly CancellationToken _cancellationToken;

            internal BackgroundRevalidationSink(long maxBodyBytes, CancellationToken cancellationToken)
            {
                _maxBodyBytes = maxBodyBytes;
                _cancellationToken = cancellationToken;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (body == null)
                    return;

                if (body.TryGetBufferedData(out _))
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                    return;
                }

                var completed = false;
                var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);
                try
                {
                    long total = 0;
                    while (true)
                    {
                        var read = await body.ReadAsync(buffer.AsMemory(0, ReadBufferBytes), _cancellationToken)
                            .ConfigureAwait(false);
                        if (read == 0)
                        {
                            completed = true;
                            break;
                        }

                        total += read;
                        if (total > _maxBodyBytes)
                            break;
                    }
                }
                catch
                {
                    body.Abort();
                    throw;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (completed)
                    await body.DisposeAsync().ConfigureAwait(false);
                else
                    body.Abort();
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }
    }
}
