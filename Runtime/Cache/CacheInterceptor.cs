using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core.Internal;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    public sealed class CachePolicy
    {
        public bool EnableCache { get; set; } = true;
        public bool CacheHeadRequests { get; set; }
        public bool EnableRevalidation { get; set; } = true;
        public bool DoNotCacheWithoutFreshness { get; set; } = true;
        public bool EnableHeuristicFreshness { get; set; }
        public TimeSpan HeuristicFreshnessLifetime { get; set; } = TimeSpan.FromMinutes(1);
        public bool AllowPrivateResponses { get; set; } = true;
        public bool AllowCacheForAuthorizedRequests { get; set; }
        public bool AllowSetCookieResponses { get; set; }
        public bool AllowVaryCookie { get; set; }
        public bool AllowVaryAuthorization { get; set; }
        public bool InvalidateOnUnsafeMethods { get; set; } = true;
        public ICacheStorage Storage { get; set; } = new MemoryCacheStorage();
    }

    /// <summary>
    /// RFC-aware cache interceptor with conditional revalidation support.
    /// </summary>
    public sealed partial class CacheInterceptor : IHttpInterceptor, IDisposable
    {
        private const string EmptyVaryKeyToken = "~";
        private const int MaxVaryHeaders = 32;

        private static readonly string[] SensitiveVaryHeaders =
        {
            "authorization",
            "cookie"
        };

        private static readonly string[] HopByHopHeaderNames =
        {
            "Connection",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "Proxy-Connection",
            "TE",
            "Trailer",
            "Transfer-Encoding",
            "Upgrade"
        };

        private readonly CachePolicy _policy;
        private readonly bool _ownsStorage;
        private readonly object _indexLock = new object();
        // Variant index is an in-memory accelerator only. Entries are ref-counted per
        // signature and cleaned up as storage keys are removed or invalidated.
        // This index is intentionally not persisted across process restarts in v1.
        private readonly Dictionary<string, VariantBucket> _variantIndex = new Dictionary<string, VariantBucket>(StringComparer.Ordinal);
        private int _disposed;

        private sealed class VariantBucket
        {
            public readonly HashSet<string> Signatures = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> StorageKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> SignatureByStorageKey = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> SignatureRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private readonly struct CacheLookupResult
        {
            public readonly CacheEntry Entry;
            public readonly string StorageKey;
            public readonly string Signature;

            public CacheLookupResult(CacheEntry entry, string storageKey, string signature)
            {
                Entry = entry;
                StorageKey = storageKey;
                Signature = signature;
            }
        }

        private readonly struct PreparedCacheEntry
        {
            public readonly CacheEntry Entry;
            public readonly string Signature;

            public PreparedCacheEntry(CacheEntry entry, string signature)
            {
                Entry = entry;
                Signature = signature;
            }
        }

        private readonly struct CacheControlDirectives
        {
            public readonly bool NoStore;
            public readonly bool NoCache;
            public readonly bool Private;
            public readonly bool Public;
            public readonly bool MustRevalidate;
            public readonly TimeSpan? SharedMaxAge;
            public readonly TimeSpan? MaxAge;
            public readonly TimeSpan? StaleWhileRevalidate;

            public CacheControlDirectives(
                bool noStore,
                bool noCache,
                bool @private,
                bool @public,
                bool mustRevalidate,
                TimeSpan? sharedMaxAge,
                TimeSpan? maxAge,
                TimeSpan? staleWhileRevalidate)
            {
                NoStore = noStore;
                NoCache = noCache;
                Private = @private;
                Public = @public;
                MustRevalidate = mustRevalidate;
                SharedMaxAge = sharedMaxAge;
                MaxAge = maxAge;
                StaleWhileRevalidate = staleWhileRevalidate;
            }
        }

        public CacheInterceptor(CachePolicy policy = null)
        {
            if (policy == null)
            {
                _policy = new CachePolicy();
                _ownsStorage = true;
            }
            else
            {
                _policy = policy;
                _ownsStorage = false;
            }

            if (_policy.Storage == null)
                throw new ArgumentNullException(nameof(policy), "CachePolicy.Storage cannot be null.");
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                ThrowIfDisposed();

                if (IsUnsafeMethod(request.Method))
                {
                    await HandleUnsafeInvalidationAsync(request, handler, context, next, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!CanCacheMethod(request.Method) || !_policy.EnableCache)
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var requestCacheControl = ParseCacheControl(request.Headers.GetValues("Cache-Control"));
                var forceRevalidation = requestCacheControl.NoCache || HasPragmaNoCache(request.Headers);
                var baseKey = BuildBaseKey(request.Method, request.Uri);
                var lookup = await TryLookupAsync(request, baseKey, cancellationToken).ConfigureAwait(false);
                if (lookup.Entry != null)
                {
                    var nowUtc = DateTime.UtcNow;
                    if (forceRevalidation)
                    {
                        if (_policy.EnableRevalidation && lookup.Entry.CanRevalidate())
                        {
                            await RevalidateAsync(
                                request,
                                handler,
                                context,
                                next,
                                cancellationToken,
                                baseKey,
                                lookup).ConfigureAwait(false);
                            return;
                        }
                    }
                    else
                    {
                        if (lookup.Entry.IsFresh(nowUtc))
                        {
                            context.RecordEvent("CacheHit", new Dictionary<string, object>
                            {
                                { "key", baseKey }
                            });
                            ServeCachedEntry(handler, lookup.Entry, request, context, "HIT");
                            return;
                        }

                        if (_policy.EnableRevalidation && lookup.Entry.IsStaleWhileRevalidate(nowUtc))
                        {
                            context.RecordEvent("CacheHit", new Dictionary<string, object>
                            {
                                { "key", baseKey }
                            });
                            ServeCachedEntry(handler, lookup.Entry, request, context, "STALE");

                            var revalidationRequest = request.Clone();
                            StartBackgroundRevalidation(revalidationRequest, next, baseKey, lookup);
                            return;
                        }

                        if (_policy.EnableRevalidation && lookup.Entry.CanRevalidate())
                        {
                            await RevalidateAsync(
                                request,
                                handler,
                                context,
                                next,
                                cancellationToken,
                                baseKey,
                                lookup).ConfigureAwait(false);
                            return;
                        }

                        await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                        UnregisterStoredVariant(baseKey, lookup.StorageKey);
                    }
                }

                context.RecordEvent("CacheMiss", new Dictionary<string, object>
                {
                    { "key", baseKey }
                });

                await next(
                    request,
                    new CacheStoringHandler(handler, this, request, baseKey, context),
                    context,
                    cancellationToken).ConfigureAwait(false);
            };
        }

        private async Task HandleUnsafeInvalidationAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            DispatchFunc next,
            CancellationToken cancellationToken)
        {
            using var response = await CollectBufferedResponseAsync(
                next,
                request,
                context,
                cancellationToken).ConfigureAwait(false);

            if (_policy.InvalidateOnUnsafeMethods && (int)response.StatusCode < 500)
            {
                var invalidationTargets = CollectInvalidationTargets(request.Uri, response.Headers);
                for (int i = 0; i < invalidationTargets.Count; i++)
                    await InvalidateUriAsync(invalidationTargets[i], cancellationToken).ConfigureAwait(false);

                context.RecordEvent("CacheInvalidated", new Dictionary<string, object>
                {
                    { "uri", NormalizeUri(request.Uri) },
                    { "count", invalidationTargets.Count }
                });
            }

            ReplayCollectedResponse(handler, request, response, context);
        }

        private async Task RevalidateAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            DispatchFunc next,
            CancellationToken cancellationToken,
            string baseKey,
            CacheLookupResult lookup)
        {
            context.RecordEvent("CacheRevalidate", new Dictionary<string, object>
            {
                { "key", lookup.StorageKey }
            });

            var conditionalHeaders = request.Headers.Clone();
            if (!string.IsNullOrEmpty(lookup.Entry.ETag))
                conditionalHeaders.Set("If-None-Match", lookup.Entry.ETag);
            if (!string.IsNullOrEmpty(lookup.Entry.LastModified))
                conditionalHeaders.Set("If-Modified-Since", lookup.Entry.LastModified);

            var conditionalRequest = CloneRequest(
                request,
                method: request.Method,
                uri: request.Uri,
                headers: conditionalHeaders,
                body: request.Body);

            context.UpdateRequest(conditionalRequest);

            using var response = await CollectBufferedResponseAsync(
                next,
                conditionalRequest,
                context,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.NotModified)
            {
                context.RecordEvent("CacheRevalidateModified");

                bool replacedCachedEntry = false;
                if (ShouldConsiderForStorage(request, response))
                {
                    var prepared = PrepareEntryForStorage(request, response, baseKey);
                    if (prepared.Entry != null)
                    {
                        await _policy.Storage
                            .SetAsync(prepared.Entry.Key, prepared.Entry, cancellationToken)
                            .ConfigureAwait(false);
                        RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);
                        replacedCachedEntry = true;

                        if (!string.Equals(lookup.StorageKey, prepared.Entry.Key, StringComparison.Ordinal))
                        {
                            await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                            UnregisterStoredVariant(baseKey, lookup.StorageKey);
                        }
                    }
                }

                if (!replacedCachedEntry)
                {
                    // The upstream response is authoritative and this cache entry is no longer valid.
                    await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                    UnregisterStoredVariant(baseKey, lookup.StorageKey);
                }

                ReplayCollectedResponse(handler, conditionalRequest, response, context);
                return;
            }

            context.RecordEvent("CacheRevalidateNotModified");

            var merged = MergeNotModifiedEntry(request, lookup.Entry, response, baseKey);
            if (merged.Entry != null)
            {
                if (!string.Equals(lookup.StorageKey, merged.Entry.Key, StringComparison.Ordinal))
                {
                    await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                    UnregisterStoredVariant(baseKey, lookup.StorageKey);
                }

                await _policy.Storage
                    .SetAsync(merged.Entry.Key, merged.Entry, cancellationToken)
                    .ConfigureAwait(false);
                RegisterStoredVariant(baseKey, merged.Signature, merged.Entry.Key);

                ServeCachedEntry(handler, merged.Entry, request, context, "REVALIDATED");
                return;
            }

            await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
            UnregisterStoredVariant(baseKey, lookup.StorageKey);
            ServeCachedEntry(handler, lookup.Entry, request, context, "REVALIDATED");
        }

        private void StartBackgroundRevalidation(
            UHttpRequest request,
            DispatchFunc next,
            string baseKey,
            CacheLookupResult lookup)
        {
            _ = RunBackgroundRevalidationAsync(request, next, baseKey, lookup);
        }

        private async Task RunBackgroundRevalidationAsync(
            UHttpRequest request,
            DispatchFunc next,
            string baseKey,
            CacheLookupResult lookup)
        {
            // Detach from the foreground cache-hit path before starting revalidation work.
            await Task.Yield();

            var context = RequestContext.CreateForBackground(request);
            try
            {
                await RevalidateAsync(
                    request,
                    NullHttpHandler.Instance,
                    context,
                    next,
                    CancellationToken.None,
                    baseKey,
                    lookup).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TurboHTTP][Cache] Background revalidation failed: " + ex);
            }
            finally
            {
                context.Stop();
                context.Clear();
                request.Dispose();
            }
        }

        private async Task InvalidateUriAsync(Uri uri, CancellationToken cancellationToken)
        {
            var getBase = BuildBaseKey(HttpMethod.GET, uri);
            var headBase = BuildBaseKey(HttpMethod.HEAD, uri);

            var keys = new HashSet<string>(StringComparer.Ordinal)
            {
                BuildStorageKey(getBase, string.Empty),
                BuildStorageKey(headBase, string.Empty)
            };

            foreach (var key in TakeStorageKeys(getBase))
                keys.Add(key);
            foreach (var key in TakeStorageKeys(headBase))
                keys.Add(key);

            foreach (var key in keys)
                await _policy.Storage.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        private async Task<CacheLookupResult> TryLookupAsync(
            UHttpRequest request,
            string baseKey,
            CancellationToken cancellationToken)
        {
            var signatures = GetSignatureSnapshot(baseKey);
            if (signatures.Length == 0)
                signatures = new[] { string.Empty };

            for (int i = 0; i < signatures.Length; i++)
            {
                var signature = signatures[i];
                var varyHeaders = ParseSignature(signature);
                var varyKey = BuildVaryKey(request.Headers, varyHeaders);
                var storageKey = BuildStorageKey(baseKey, varyKey);

                var entry = await _policy.Storage.GetAsync(storageKey, cancellationToken).ConfigureAwait(false);
                if (entry == null)
                {
                    UnregisterStoredVariant(baseKey, storageKey);
                    continue;
                }

                return new CacheLookupResult(entry, storageKey, signature);
            }

            return default;
        }

        private PreparedCacheEntry PrepareEntryForStorage(UHttpRequest request, UHttpResponse response, string baseKey)
        {
            return PrepareEntryForStorage(
                request.Uri,
                request.Headers,
                response.StatusCode,
                response.Headers,
                SnapshotBodyForCache(response.Body),
                response.Request?.Uri ?? request.Uri,
                baseKey);
        }

        private PreparedCacheEntry PrepareEntryForStorage(
            Uri requestUri,
            HttpHeaders requestHeaders,
            HttpStatusCode statusCode,
            HttpHeaders responseHeaders,
            ReadOnlyMemory<byte> bodySnapshot,
            Uri responseUrl,
            string baseKey)
        {
            requestHeaders = requestHeaders ?? new HttpHeaders();

            var responseHeadersForStorage = responseHeaders?.Clone() ?? new HttpHeaders();
            StripHopByHopHeaders(responseHeadersForStorage);

            if (!_policy.AllowSetCookieResponses && responseHeadersForStorage.Contains("Set-Cookie"))
                return default;

            var responseCacheControl = ParseCacheControl(responseHeadersForStorage.GetValues("Cache-Control"));
            var requestCacheControl = ParseCacheControl(requestHeaders.GetValues("Cache-Control"));

            if (!_policy.AllowCacheForAuthorizedRequests
                && requestHeaders.Contains("Authorization")
                && !responseCacheControl.Public)
            {
                return default;
            }

            if (responseCacheControl.NoStore || requestCacheControl.NoStore)
                return default;

            if (responseCacheControl.Private && !_policy.AllowPrivateResponses)
                return default;

            if (!TryResolveVary(responseHeadersForStorage, out var varyHeaders, out var varyIsWildcard))
                return default;
            if (varyIsWildcard)
                return default;

            if (TryResolveSensitiveVaryFlags(varyHeaders, out var hasCookieVary, out var hasAuthVary))
            {
                if ((hasCookieVary && !_policy.AllowVaryCookie) || (hasAuthVary && !_policy.AllowVaryAuthorization))
                    return default;
            }

            var nowUtc = DateTime.UtcNow;
            var expiresAtUtc = ResolveExpiry(nowUtc, responseCacheControl, responseHeadersForStorage, isSharedCache: false);
            var eTag = responseHeadersForStorage.Get("ETag");
            var lastModified = responseHeadersForStorage.Get("Last-Modified");
            var pragmaNoCache = HasPragmaNoCache(responseHeadersForStorage);
            var mustRevalidate = responseCacheControl.NoCache || responseCacheControl.MustRevalidate || pragmaNoCache;

            if (!expiresAtUtc.HasValue)
            {
                if (mustRevalidate && (!string.IsNullOrEmpty(eTag) || !string.IsNullOrEmpty(lastModified)))
                {
                    // `no-cache` style entries can be stored without freshness metadata
                    // as long as validators are present; middleware will revalidate on read.
                }
                else if (_policy.DoNotCacheWithoutFreshness)
                {
                    return default;
                }

                if (!expiresAtUtc.HasValue && _policy.EnableHeuristicFreshness)
                    expiresAtUtc = nowUtc.Add(_policy.HeuristicFreshnessLifetime);
            }

            var varyKey = BuildVaryKey(requestHeaders, varyHeaders);
            var storageKey = BuildStorageKey(baseKey, varyKey);
            var signature = BuildSignature(varyHeaders);

            var entry = new CacheEntry(
                key: storageKey,
                statusCode: statusCode,
                headers: responseHeadersForStorage,
                body: bodySnapshot,
                cachedAtUtc: nowUtc,
                expiresAtUtc: expiresAtUtc,
                eTag: eTag,
                lastModified: lastModified,
                responseUrl: responseUrl ?? requestUri,
                varyHeaders: varyHeaders,
                varyKey: varyKey,
                staleWhileRevalidate: responseCacheControl.StaleWhileRevalidate,
                mustRevalidate: mustRevalidate);

            return new PreparedCacheEntry(entry, signature);
        }

        private PreparedCacheEntry MergeNotModifiedEntry(
            UHttpRequest request,
            CacheEntry existing,
            UHttpResponse notModifiedResponse,
            string baseKey)
        {
            var mergedHeaders = existing.Headers.Clone();
            foreach (var name in notModifiedResponse.Headers.Names)
            {
                if (!notModifiedResponse.Headers.Contains(name))
                    continue;

                mergedHeaders.Remove(name);
                var values = notModifiedResponse.Headers.GetValues(name);
                for (int j = 0; j < values.Count; j++)
                    mergedHeaders.Add(name, values[j]);
            }

            StripHopByHopHeaders(mergedHeaders);

            if (!TryResolveVary(mergedHeaders, out var varyHeaders, out var varyIsWildcard))
                return default;
            if (varyIsWildcard)
                return default;

            if (TryResolveSensitiveVaryFlags(varyHeaders, out var hasCookieVary, out var hasAuthVary))
            {
                if ((hasCookieVary && !_policy.AllowVaryCookie) || (hasAuthVary && !_policy.AllowVaryAuthorization))
                    return default;
            }

            var nowUtc = DateTime.UtcNow;
            var cacheControl = ParseCacheControl(mergedHeaders.GetValues("Cache-Control"));
            var expiresAtUtc = ResolveExpiry(nowUtc, cacheControl, mergedHeaders, isSharedCache: false);
            var eTag = mergedHeaders.Get("ETag");
            var lastModified = mergedHeaders.Get("Last-Modified");
            var mustRevalidate = cacheControl.NoCache || cacheControl.MustRevalidate || HasPragmaNoCache(mergedHeaders);

            if (!expiresAtUtc.HasValue)
            {
                if (mustRevalidate && (!string.IsNullOrEmpty(eTag) || !string.IsNullOrEmpty(lastModified)))
                {
                    // Validator-backed entries may remain freshness-less and revalidate on access.
                }
                else if (_policy.DoNotCacheWithoutFreshness)
                {
                    return default;
                }

                if (!expiresAtUtc.HasValue && _policy.EnableHeuristicFreshness)
                    expiresAtUtc = nowUtc.Add(_policy.HeuristicFreshnessLifetime);
            }

            var varyKey = BuildVaryKey(request.Headers, varyHeaders);
            var storageKey = BuildStorageKey(baseKey, varyKey);
            var signature = BuildSignature(varyHeaders);

            var mergedEntry = new CacheEntry(
                key: storageKey,
                statusCode: existing.StatusCode,
                headers: mergedHeaders,
                body: existing.Body,
                cachedAtUtc: nowUtc,
                expiresAtUtc: expiresAtUtc,
                eTag: eTag,
                lastModified: lastModified,
                responseUrl: existing.ResponseUrl,
                varyHeaders: varyHeaders,
                varyKey: varyKey,
                staleWhileRevalidate: cacheControl.StaleWhileRevalidate,
                mustRevalidate: mustRevalidate);

            return new PreparedCacheEntry(mergedEntry, signature);
        }

        private void ServeCachedEntry(
            IHttpHandler handler,
            CacheEntry entry,
            UHttpRequest request,
            RequestContext context,
            string provenance)
        {
            var headers = entry.Headers.Clone();
            var ageSeconds = ComputeCurrentAgeSeconds(entry, DateTime.UtcNow);
            headers.Set("Age", ageSeconds.ToString(CultureInfo.InvariantCulture));
            headers.Set("X-Cache", provenance);

            handler.OnRequestStart(request, context);
            handler.OnResponseStart((int)entry.StatusCode, headers, context);

            if (!entry.Body.IsEmpty)
                handler.OnResponseData(entry.Body.Span, context);

            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }

        private static bool ShouldConsiderForStorage(UHttpRequest request, UHttpResponse response)
        {
            return response != null
                && ShouldConsiderForStorage(request.Method, response.StatusCode);
        }

        private static bool ShouldConsiderForStorage(HttpMethod requestMethod, HttpStatusCode responseStatusCode)
        {
            var numericStatusCode = (int)responseStatusCode;
            return ((numericStatusCode >= 200 && numericStatusCode < 300)
                    || IsRfc9111CacheableByDefault(responseStatusCode))
                   && requestMethod != HttpMethod.OPTIONS;
        }

        private bool CanCacheMethod(HttpMethod method)
        {
            if (method == HttpMethod.GET)
                return true;

            return _policy.CacheHeadRequests && method == HttpMethod.HEAD;
        }

        private static bool IsUnsafeMethod(HttpMethod method)
        {
            return method == HttpMethod.POST
                   || method == HttpMethod.PUT
                   || method == HttpMethod.PATCH
                   || method == HttpMethod.DELETE;
        }

        private static UHttpRequest CloneRequest(
            UHttpRequest source,
            HttpMethod method,
            Uri uri,
            HttpHeaders headers,
            ReadOnlyMemory<byte> body)
        {
            return new UHttpRequest(
                method,
                uri,
                headers,
                body.IsEmpty ? null : body.ToArray(),
                source.Timeout,
                source.Metadata);
        }
        private static ReadOnlyMemory<byte> SnapshotBodyForCache(ReadOnlySequence<byte> source)
        {
            if (source.IsEmpty)
                return ReadOnlyMemory<byte>.Empty;

            return source.ToArray();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_ownsStorage && _policy.Storage is IDisposable disposableStorage)
                disposableStorage.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(CacheInterceptor));
        }

        internal void QueueStoreResponse(
            HttpMethod requestMethod,
            Uri requestUri,
            HttpHeaders requestHeaders,
            string baseKey,
            int statusCode,
            HttpHeaders responseHeaders,
            SegmentedBuffer body)
        {
            _ = StoreResponseAsync(requestMethod, requestUri, requestHeaders, baseKey, statusCode, responseHeaders, body);
        }

        private async Task StoreResponseAsync(
            HttpMethod requestMethod,
            Uri requestUri,
            HttpHeaders requestHeaders,
            string baseKey,
            int statusCode,
            HttpHeaders responseHeaders,
            SegmentedBuffer body)
        {
            // Detach from the response completion callback before snapshotting the response body.
            await Task.Yield();

            try
            {
                var statusCodeForStorage = (HttpStatusCode)statusCode;
                if (!ShouldConsiderForStorage(requestMethod, statusCodeForStorage))
                    return;

                var bodySnapshot = body == null
                    ? ReadOnlyMemory<byte>.Empty
                    : body.AsSequence().ToArray();
                var prepared = PrepareEntryForStorage(
                    requestUri,
                    requestHeaders,
                    statusCodeForStorage,
                    responseHeaders,
                    bodySnapshot,
                    requestUri,
                    baseKey);
                if (prepared.Entry == null)
                    return;

                await StorePreparedEntryAsync(baseKey, prepared).ConfigureAwait(false);
            }
            finally
            {
                body?.Dispose();
            }
        }

        private async Task StorePreparedEntryAsync(string baseKey, PreparedCacheEntry prepared)
        {
            try
            {
                await _policy.Storage.SetAsync(prepared.Entry.Key, prepared.Entry, CancellationToken.None).ConfigureAwait(false);
                RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TurboHTTP][Cache] Store failed: " + ex);
            }
        }

        private static void ReplayCollectedResponse(
            IHttpHandler handler,
            UHttpRequest request,
            UHttpResponse response,
            RequestContext context)
        {
            handler.OnRequestStart(request, context);
            handler.OnResponseStart((int)response.StatusCode, response.Headers, context);

            var body = response.Body;
            if (body.IsSingleSegment)
            {
                if (!body.FirstSpan.IsEmpty)
                    handler.OnResponseData(body.FirstSpan, context);
            }
            else
            {
                foreach (ReadOnlyMemory<byte> segment in body)
                {
                    if (!segment.Span.IsEmpty)
                        handler.OnResponseData(segment.Span, context);
                }
            }

            handler.OnResponseEnd(HttpHeaders.Empty, context);
        }

        private static Task<UHttpResponse> CollectBufferedResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            var collector = new BufferedResponseHandler(request, context);
            Task dispatchTask;
            try
            {
                dispatchTask = dispatch(request, collector, context, cancellationToken);
            }
            catch (Exception ex)
            {
                collector.Fail(ex);
                return collector.ResponseTask;
            }

            _ = dispatchTask.ContinueWith(
                static (task, state) =>
                {
                    var responseCollector = (BufferedResponseHandler)state;
                    if (task.IsFaulted)
                    {
                        responseCollector.Fail(task.Exception.GetBaseException());
                        return;
                    }

                    if (task.IsCanceled)
                    {
                        try
                        {
                            task.GetAwaiter().GetResult();
                        }
                        catch (OperationCanceledException ex)
                        {
                            responseCollector.Fail(ex);
                            return;
                        }
                    }

                    responseCollector.EnsureCompleted();
                },
                collector,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return collector.ResponseTask;
        }

        private sealed class BufferedResponseHandler : IHttpHandler
        {
            private readonly TaskCompletionSource<UHttpResponse> _tcs =
                new TaskCompletionSource<UHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly UHttpRequest _request;
            private readonly RequestContext _context;
            private SegmentedBuffer _body;
            private int _statusCode;
            private HttpHeaders _headers;

            internal BufferedResponseHandler(UHttpRequest request, RequestContext context)
            {
                _request = request;
                _context = context;
            }

            internal Task<UHttpResponse> ResponseTask => _tcs.Task;

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                _statusCode = statusCode;
                _headers = headers;
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                if (chunk.IsEmpty)
                    return;

                if (_body == null)
                    _body = new SegmentedBuffer();

                _body.Write(chunk);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                using (_body)
                {
                    var bytes = _body?.AsSequence().ToArray();
                    _tcs.TrySetResult(new UHttpResponse(
                        (HttpStatusCode)_statusCode,
                        _headers ?? new HttpHeaders(),
                        bytes,
                        _context.Elapsed,
                        _request));
                }

                _body = null;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _body?.Dispose();
                _body = null;
                _tcs.TrySetException(error);
            }

            internal void Fail(Exception ex)
            {
                _body?.Dispose();
                _body = null;
                _tcs.TrySetException(ex);
            }

            internal void EnsureCompleted()
            {
                if (!_tcs.Task.IsCompleted)
                {
                    Fail(new InvalidOperationException("Dispatch completed without a response."));
                }
            }
        }

        private sealed class NullHttpHandler : IHttpHandler
        {
            internal static readonly NullHttpHandler Instance = new NullHttpHandler();

            private NullHttpHandler()
            {
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }
    }
}
