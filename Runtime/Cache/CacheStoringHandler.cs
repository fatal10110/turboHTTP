using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Cache
{
    internal sealed class CacheStoringHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CacheInterceptor _owner;
        // Snapshot of the request the cache looked up (method, target and headers at lookup time).
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        private readonly string _baseKey;
        private readonly long _maxCacheableResponseBodyBytes;
        // RFC 9111 section 4.2.3 request_time: when the request that elicits the response was sent (#306).
        private readonly DateTime _requestTimeUtc;

        // The effective request: what the interceptors inside the cache actually sent (#494, #495). Written by
        // OnRequestStart (once per attempt or redirect hop dispatched inside the cache) and read by
        // OnResponseStartAsync of the same dispatch, which the transport calls after OnRequestStart; Volatile for
        // the ARM64 and 32-bit IL2CPP memory models. _effectiveHeaders stays null when OnRequestStart never ran.
        private HttpHeaders _effectiveHeaders;
        // 1 once any request sent inside the cache had another method or target URI than the looked-up one
        // (a redirect hop, a URI rewrite): the response is not the looked-up resource's and is never stored.
        private int _targetChanged;

        /// <param name="requestTimeUtc">
        /// When the request was sent. Null means now: the handler is created right before the request is dispatched.
        /// A handler created after the response arrived (200 after revalidation) passes the time captured before
        /// the conditional request was sent.
        /// </param>
        internal CacheStoringHandler(
            IHttpHandler inner,
            CacheInterceptor owner,
            UHttpRequest request,
            string baseKey,
            RequestContext context,
            DateTime? requestTimeUtc = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _requestMethod = request.Method;
            _requestUri = request.Uri;
            _requestHeaders = request.Headers.Clone();
            _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            _maxCacheableResponseBodyBytes = owner.MaxCacheableResponseBodyBytes;
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _requestTimeUtc = requestTimeUtc ?? owner.GetUtcNow();
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            CaptureEffectiveRequest(request);
            _inner.OnRequestStart(request, context);
        }

        /// <summary>
        /// Records the request as it left the pipeline. Interceptors inside the cache may copy and change the
        /// request (add credentials, follow a redirect), so the storage guards and the Vary key must use this
        /// request rather than the one the cache looked up (RFC 9111 sections 3, 3.5 and 4.1; #494, #495).
        /// </summary>
        internal void CaptureEffectiveRequest(UHttpRequest request)
        {
            if (request == null)
                return;

            if (!IsSameTarget(request, _requestMethod, _requestUri))
                MarkTargetChanged();

            // Cloned: the request may be mutated or returned to a pool once this hop completes.
            Volatile.Write(ref _effectiveHeaders, request.Headers.Clone());
        }

        /// <summary>A request sent inside the cache had another target than the looked-up one (#494).</summary>
        internal void MarkTargetChanged()
        {
            Volatile.Write(ref _targetChanged, 1);
        }

        /// <summary>True when <paramref name="request"/> has the looked-up method and target URI.</summary>
        internal static bool IsSameTarget(UHttpRequest request, HttpMethod method, Uri uri)
        {
            if (request.Method != method)
                return false;

            var effective = request.Uri;
            if (ReferenceEquals(effective, uri))
                return true;

            return effective != null && uri != null &&
                   string.Equals(effective.AbsoluteUri, uri.AbsoluteUri, StringComparison.Ordinal);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            var capturedHeaders = headers?.Clone() ?? new HttpHeaders();
            // #494: a response to another target (a redirect followed inside the cache) is not the looked-up
            // resource's response, so it is delivered but never stored under the looked-up key.
            if (Volatile.Read(ref _targetChanged) != 0 ||
                !CacheInterceptor.ShouldConsiderForStorage(_requestMethod, statusCode))
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            if (body == null)
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            if (body.Length.HasValue && body.Length.Value > _maxCacheableResponseBodyBytes)
            {
                await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                return;
            }

            // RFC 9111 section 4.2.3 response_time: the headers have arrived.
            var responseTimeUtc = _owner.GetUtcNow();
            // #495: the storage guards and the Vary key use the effective request; the lookup-time headers go along
            // so the store is refused when a Vary-nominated header differs between the two.
            var effectiveHeaders = Volatile.Read(ref _effectiveHeaders);
            var storeRequestHeaders = effectiveHeaders ?? _requestHeaders;
            var lookupRequestHeaders = effectiveHeaders != null ? _requestHeaders : null;
            if (body.TryGetBufferedData(out var buffered))
            {
                if ((long)buffered.Length > _maxCacheableResponseBodyBytes)
                {
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                    return;
                }

                // A fully buffered body has its trailers already (#155). Read them before the consumer can
                // consume or dispose the body; when they cannot be read the response is delivered but not stored.
                HttpHeaders trailersToStore;
                try
                {
                    var trailers = await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                    trailersToStore = trailers == null || trailers.Count == 0 ? null : trailers.Clone();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP][Cache] Response not stored: trailers unavailable", ex);
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                    return;
                }

                // The consumer owns capturedHeaders (UHttpResponse.Headers) and may mutate it while the background
                // store runs; the store gets its own copy, taken before the consumer sees the headers (#61).
                var headersToStore = capturedHeaders.Clone();
                SegmentedBuffer bodyToStore = null;
                if (!buffered.IsEmpty)
                {
                    bodyToStore = new SegmentedBuffer();
                    bodyToStore.Write(buffered.Span);
                }

                try
                {
                    await _inner.OnResponseStartAsync(statusCode, capturedHeaders, body, context).ConfigureAwait(false);
                }
                catch
                {
                    bodyToStore?.Dispose();
                    throw;
                }

                try
                {
                    _owner.QueueStoreResponse(
                        _requestMethod,
                        _requestUri,
                        storeRequestHeaders,
                        _baseKey,
                        statusCode,
                        headersToStore,
                        bodyToStore,
                        trailersToStore,
                        responseTimeUtc,
                        _requestTimeUtc,
                        lookupRequestHeaders);
                    bodyToStore = null;
                }
                catch (Exception ex)
                {
                    bodyToStore?.Dispose();
                    TurboDiagnostics.Log("[TurboHTTP][Cache] Failed to queue cache store", ex);
                }

                return;
            }

            var tee = new TeeBodySource(
                body,
                _owner,
                _requestMethod,
                _requestUri,
                storeRequestHeaders,
                _baseKey,
                statusCode,
                capturedHeaders,
                responseTimeUtc,
                _requestTimeUtc,
                _maxCacheableResponseBodyBytes,
                lookupRequestHeaders);

            await _inner.OnResponseStartAsync(statusCode, capturedHeaders, tee, context).ConfigureAwait(false);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
        }
    }

    internal sealed class TeeBodySource : IResponseBodySource
    {
        private static readonly TimeSpan TrailerLoadTimeout = TimeSpan.FromSeconds(2);

        private readonly IResponseBodySource _inner;
        private readonly CacheInterceptor _owner;
        private readonly HttpMethod _requestMethod;
        private readonly Uri _requestUri;
        private readonly HttpHeaders _requestHeaders;
        // Lookup-time request headers when they may differ from _requestHeaders (the effective request), else null (#495).
        private readonly HttpHeaders _lookupRequestHeaders;
        private readonly string _baseKey;
        private readonly int _statusCode;
        private readonly HttpHeaders _responseHeaders;
        private readonly DateTime _responseTimeUtc;
        private readonly DateTime _requestTimeUtc;
        private readonly long _maxCacheableResponseBodyBytes;
        // Body length the inner source announced (Content-Length), or null when unknown or when the response has
        // no content by definition. A naturally ended body of another length is not stored (#496).
        private readonly long? _expectedBodyLength;

        // Single owner (#497): the field holds the accumulator only while no Write is in flight. Accumulate takes it
        // out of the field for the Write and publishes it back afterwards; Abort, DisposeAsync and the store detach
        // set _accumulatorDetached and dispose only what they take out of the field. Whoever takes it out disposes
        // (or detaches) it exactly once, so pooled segments are never returned while a Write copies into them.
        private IStreamingCacheAccumulator _accumulator;
        private int _accumulatorDetached;
        // Written by the reader only (Accumulate); Volatile for the 64-bit field on 32-bit IL2CPP.
        private long _accumulatedBytes;
        private HttpHeaders _trailers;
        // Copy for the store, taken before the consumer receives _trailers (#61/#155); null when there are none.
        private HttpHeaders _trailersToStore;
        private int _trailersLoaded;
        private int _completedNaturally;
        private int _aborted;
        private int _disposed;

        internal TeeBodySource(
            IResponseBodySource inner,
            CacheInterceptor owner,
            HttpMethod requestMethod,
            Uri requestUri,
            HttpHeaders requestHeaders,
            string baseKey,
            int statusCode,
            HttpHeaders responseHeaders,
            DateTime responseTimeUtc,
            DateTime requestTimeUtc,
            long maxCacheableResponseBodyBytes,
            HttpHeaders lookupRequestHeaders = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _requestMethod = requestMethod;
            _requestUri = requestUri ?? throw new ArgumentNullException(nameof(requestUri));
            _requestHeaders = requestHeaders?.Clone() ?? new HttpHeaders();
            _lookupRequestHeaders = lookupRequestHeaders?.Clone();
            _baseKey = baseKey ?? throw new ArgumentNullException(nameof(baseKey));
            _statusCode = statusCode;
            _responseHeaders = responseHeaders?.Clone() ?? new HttpHeaders();
            _responseTimeUtc = responseTimeUtc;
            _requestTimeUtc = requestTimeUtc;
            if (maxCacheableResponseBodyBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCacheableResponseBodyBytes));

            _maxCacheableResponseBodyBytes = maxCacheableResponseBodyBytes;
            // RFC 9110 sections 9.3.2, 15.3.5, 15.4.5: HEAD, 204 and 304 responses have no content whatever
            // Content-Length says.
            _expectedBodyLength = requestMethod == HttpMethod.HEAD || statusCode == 204 || statusCode == 304
                ? null
                : inner.Length;
            _accumulator = owner.CreateStreamingAccumulator();
        }

        public long? Length => _inner.Length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            body = default;
            return false;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();

            try
            {
                var read = await _inner.ReadAsync(destination, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    // A read into an empty destination returns 0 without reaching the end of the body (#496).
                    if (!destination.IsEmpty)
                        Volatile.Write(ref _completedNaturally, 1);

                    return 0;
                }

                Accumulate(destination.Slice(0, read).Span);
                return read;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Abort();
                throw;
            }
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            byte[] rented = null;
            try
            {
                rented = ByteArrayPool.Rent(16 * 1024);
                while (await ReadAsync(rented, ct).ConfigureAwait(false) != 0)
                {
                }
            }
            finally
            {
                if (rented != null)
                    ByteArrayPool.Return(rented);
            }
        }

        public void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0)
                return;

            DetachAccumulator();
            _inner.Abort();
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            if (Volatile.Read(ref _trailersLoaded) != 0)
                return _trailers ?? HttpHeaders.Empty;

            // Before the end of the body the inner source drains the unread rest itself, past this tee, so the
            // accumulated bytes would be a prefix of the body. RFC 9111 section 3.3: an incomplete response is not
            // stored as a complete one, so the accumulator is dropped (#496). A body already read to its announced
            // length has nothing left to drain and stays cacheable.
            if (Volatile.Read(ref _completedNaturally) == 0 &&
                !(_expectedBodyLength.HasValue && Volatile.Read(ref _accumulatedBytes) == _expectedBodyLength.Value))
            {
                DetachAccumulator();
            }

            try
            {
                SetLoadedTrailers(await _inner.GetTrailersAsync(ct).ConfigureAwait(false));
                return _trailers ?? HttpHeaders.Empty;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Abort();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                if (Volatile.Read(ref _aborted) == 0 &&
                    Volatile.Read(ref _completedNaturally) != 0 &&
                    Volatile.Read(ref _accumulator) != null &&
                    BodyMatchesAnnouncedLength() &&
                    await EnsureTrailersLoadedForStoreAsync().ConfigureAwait(false))
                {
                    var bodyToStore = DetachAccumulatorForStore();
                    if (bodyToStore == null)
                        return;

                    try
                    {
                        _owner.QueueStoreResponse(
                            _requestMethod,
                            _requestUri,
                            _requestHeaders,
                            _baseKey,
                            _statusCode,
                            _responseHeaders,
                            bodyToStore,
                            _trailersToStore,
                            _responseTimeUtc,
                            _requestTimeUtc,
                            _lookupRequestHeaders);
                        bodyToStore = null;
                    }
                    catch (Exception ex)
                    {
                        TurboDiagnostics.Log("[TurboHTTP][Cache] Failed to queue cache store", ex);
                    }
                    finally
                    {
                        bodyToStore?.Dispose();
                    }
                }
            }
            finally
            {
                DetachAccumulator();
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void Accumulate(ReadOnlySpan<byte> chunk)
        {
            if (chunk.IsEmpty)
                return;

            // Take the accumulator out of the field for the Write: a concurrent Abort/DisposeAsync then finds the
            // field empty and leaves the dispose to this thread (#497).
            var accumulator = Interlocked.Exchange(ref _accumulator, null);
            if (accumulator == null)
                return;

            var accumulatedBytes = _accumulatedBytes;
            if (accumulatedBytes > _maxCacheableResponseBodyBytes - chunk.Length)
            {
                Interlocked.Exchange(ref _accumulatorDetached, 1);
                accumulator.Dispose();
                return;
            }

            Exception writeError = null;
            try
            {
                accumulator.Write(chunk);
            }
            catch (Exception ex)
            {
                writeError = ex;
            }

            if (writeError != null)
            {
                TurboDiagnostics.Log("[TurboHTTP][Cache] Tee accumulator detached after write failure", writeError);
                Interlocked.Exchange(ref _accumulatorDetached, 1);
                accumulator.Dispose();
                return;
            }

            Volatile.Write(ref _accumulatedBytes, accumulatedBytes + chunk.Length);

            // Publish it back, then re-check: a detach that ran during the Write found the field empty. Both sides
            // use full fences (Interlocked), so at least one of them sees the other and exactly one disposes.
            Interlocked.Exchange(ref _accumulator, accumulator);
            if (Volatile.Read(ref _accumulatorDetached) != 0)
                Interlocked.Exchange(ref _accumulator, null)?.Dispose();
        }

        private bool BodyMatchesAnnouncedLength()
        {
            if (!_expectedBodyLength.HasValue || Volatile.Read(ref _accumulatedBytes) == _expectedBodyLength.Value)
                return true;

            TurboDiagnostics.Log("[TurboHTTP][Cache] Response not stored: body length differs from Content-Length");
            return false;
        }

        private async ValueTask<bool> EnsureTrailersLoadedForStoreAsync()
        {
            if (Volatile.Read(ref _trailersLoaded) != 0)
                return true;

            CancellationTokenSource timeoutCts = null;
            try
            {
                timeoutCts = new CancellationTokenSource(TrailerLoadTimeout);
                SetLoadedTrailers(await _inner.GetTrailersAsync(timeoutCts.Token).ConfigureAwait(false));
                return true;
            }
            catch
            {
                Abort();
                return false;
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }

        private void SetLoadedTrailers(HttpHeaders trailers)
        {
            _trailers = trailers;
            _trailersToStore = trailers == null || trailers.Count == 0 ? null : trailers.Clone();
            Volatile.Write(ref _trailersLoaded, 1);
        }

        private void DetachAccumulator()
        {
            // The flag first: a Write in flight holds the accumulator and disposes it when it sees the flag.
            Interlocked.Exchange(ref _accumulatorDetached, 1);
            Interlocked.Exchange(ref _accumulator, null)?.Dispose();
        }

        private SegmentedBuffer DetachAccumulatorForStore()
        {
            Interlocked.Exchange(ref _accumulatorDetached, 1);
            var accumulator = Interlocked.Exchange(ref _accumulator, null);
            if (accumulator == null)
                return null;

            try
            {
                return accumulator.DetachBuffer();
            }
            finally
            {
                accumulator.Dispose();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(TeeBodySource));
        }
    }
}
