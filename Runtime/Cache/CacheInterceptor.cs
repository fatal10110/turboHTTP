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
    /// <summary>
    /// Configuration policy for caching behavior.
    /// </summary>
    public sealed class CachePolicy
    {
        /// <summary> Gets or sets whether caching is enabled. Default true. </summary>
        public bool EnableCache { get; set; } = true;
        /// <summary> Gets or sets whether to cache HEAD requests. </summary>
        public bool CacheHeadRequests { get; set; }
        /// <summary> Gets or sets whether to revalidate stale entries. Default true. </summary>
        public bool EnableRevalidation { get; set; } = true;
        /// <summary> Gets or sets whether to require explicit freshness (e.g. max-age). Default true. </summary>
        public bool DoNotCacheWithoutFreshness { get; set; } = true;
        /// <summary> Gets or sets whether to enable heuristic freshness for responses without explicit expiration. </summary>
        public bool EnableHeuristicFreshness { get; set; }
        /// <summary> Default lifetime for conditionally cacheable responses using heuristic freshness. </summary>
        public TimeSpan HeuristicFreshnessLifetime { get; set; } = TimeSpan.FromMinutes(1);
        /// <summary> Gets or sets whether private responses can be cached. Default true. </summary>
        public bool AllowPrivateResponses { get; set; } = true;
        /// <summary> Gets or sets whether requests with Authorization headers can be cached. </summary>
        public bool AllowCacheForAuthorizedRequests { get; set; }
        /// <summary> Gets or sets whether responses with Set-Cookie can be cached. </summary>
        public bool AllowSetCookieResponses { get; set; }
        /// <summary> Gets or sets whether to allow caching when Vary: Cookie is present. </summary>
        public bool AllowVaryCookie { get; set; }
        /// <summary> Gets or sets whether to allow caching when Vary: Authorization is present. </summary>
        public bool AllowVaryAuthorization { get; set; }
        /// <summary> Gets or sets whether to invalidate cache on unsafe methods like POST, PUT, DELETE. Default true. </summary>
        public bool InvalidateOnUnsafeMethods { get; set; } = true;
        /// <summary> Gets or sets the storage backend. Defaults to in-memory storage. </summary>
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
        private readonly object _pendingMutationLock = new object();
        private readonly Dictionary<string, Task> _pendingMutations = new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _backgroundWorkCancellation = new CancellationTokenSource();
        private int _disposed;

        private sealed class VariantBucket
        {
            public readonly HashSet<string> Signatures = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> StorageKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> SignatureByStorageKey = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> SignatureRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private sealed class PendingMutationState
        {
            public readonly CacheInterceptor Owner;
            public readonly string BaseKey;

            public PendingMutationState(CacheInterceptor owner, string baseKey)
            {
                Owner = owner;
                BaseKey = baseKey;
            }
        }

        private sealed class StoreResponseState
        {
            public readonly HttpMethod RequestMethod;
            public readonly Uri RequestUri;
            public readonly HttpHeaders RequestHeaders;
            public readonly int StatusCode;
            public readonly HttpHeaders ResponseHeaders;
            public readonly SegmentedBuffer Body;

            public StoreResponseState(
                HttpMethod requestMethod,
                Uri requestUri,
                HttpHeaders requestHeaders,
                int statusCode,
                HttpHeaders responseHeaders,
                SegmentedBuffer body)
            {
                RequestMethod = requestMethod;
                RequestUri = requestUri;
                RequestHeaders = requestHeaders;
                StatusCode = statusCode;
                ResponseHeaders = responseHeaders;
                Body = body;
            }
        }

        private sealed class PreparedEntryState
        {
            public readonly PreparedCacheEntry Prepared;

            public PreparedEntryState(PreparedCacheEntry prepared)
            {
                Prepared = prepared;
            }
        }

        private sealed class ReplacementState
        {
            public readonly PreparedCacheEntry Prepared;
            public readonly string ExistingStorageKey;

            public ReplacementState(PreparedCacheEntry prepared, string existingStorageKey)
            {
                Prepared = prepared;
                ExistingStorageKey = existingStorageKey;
            }
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

        /// <summary>
        /// Initializes a new instance of the <see cref="CacheInterceptor"/> with an optional policy.
        /// </summary>
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
            try
            {
                using var response = await CollectBufferedResponseAsync(
                    next,
                    conditionalRequest,
                    context,
                    cancellationToken).ConfigureAwait(false);

                context.UpdateRequest(request);
                if (response.StatusCode != HttpStatusCode.NotModified)
                {
                    context.RecordEvent("CacheRevalidateModified");

                    bool replacedCachedEntry = false;
                    if (ShouldConsiderForStorage(request, response))
                    {
                        var prepared = PrepareEntryForStorage(request, response, baseKey);
                        if (prepared.Entry != null)
                        {
                            await ReplaceStoredEntryAsync(
                                baseKey,
                                prepared,
                                lookup.StorageKey,
                                cancellationToken).ConfigureAwait(false);
                            replacedCachedEntry = true;
                        }
                    }

                    if (!replacedCachedEntry)
                    {
                        // The upstream response is authoritative and this cache entry is no longer valid.
                        await RemoveStoredEntryAsync(baseKey, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                    }

                    ReplayCollectedResponse(handler, request, response, context);
                    return;
                }

                context.RecordEvent("CacheRevalidateNotModified");

                var merged = MergeNotModifiedEntry(request, lookup.Entry, response, baseKey);
                if (merged.Entry != null)
                {
                    if (!string.Equals(lookup.StorageKey, merged.Entry.Key, StringComparison.Ordinal))
                        await ReplaceStoredEntryAsync(baseKey, merged, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                    else
                        await StorePreparedEntryQueuedAsync(baseKey, merged, cancellationToken).ConfigureAwait(false);

                    ServeCachedEntry(handler, merged.Entry, request, context, "REVALIDATED");
                    return;
                }

                await RemoveStoredEntryAsync(baseKey, lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                ServeCachedEntry(handler, lookup.Entry, request, context, "REVALIDATED");
            }
            finally
            {
                context.UpdateRequest(request);
                conditionalRequest.Dispose();
            }
        }

        private void StartBackgroundRevalidation(
            UHttpRequest request,
            DispatchFunc next,
            string baseKey,
            CacheLookupResult lookup)
        {
            QueueBackgroundWork(
                RunBackgroundRevalidationAsync(
                    request,
                    next,
                    baseKey,
                    lookup,
                    _backgroundWorkCancellation.Token),
                "revalidation");
        }

        private async Task RunBackgroundRevalidationAsync(
            UHttpRequest request,
            DispatchFunc next,
            string baseKey,
            CacheLookupResult lookup,
            CancellationToken cancellationToken)
        {
            // Detach from the foreground cache-hit path before starting revalidation work.
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
                return;

            var context = RequestContext.CreateForBackground(request);
            try
            {
                await RevalidateAsync(
                    request,
                    NullHttpHandler.Instance,
                    context,
                    next,
                    cancellationToken,
                    baseKey,
                    lookup).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            await QueueMutationAndWaitAsync(
                getBase,
                static (owner, baseKey, token, state) => owner.InvalidateBaseKeyCoreAsync(baseKey, token),
                cancellationToken).ConfigureAwait(false);
            await QueueMutationAndWaitAsync(
                headBase,
                static (owner, baseKey, token, state) => owner.InvalidateBaseKeyCoreAsync(baseKey, token),
                cancellationToken).ConfigureAwait(false);
        }

        private async Task<CacheLookupResult> TryLookupAsync(
            UHttpRequest request,
            string baseKey,
            CancellationToken cancellationToken)
        {
            await AwaitPendingMutationAsync(baseKey, cancellationToken).ConfigureAwait(false);

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

            // Cache hits synthesize the same handler lifecycle as a network response.
            // OnRequestStart here means "response delivery begins", not "socket dispatch started".
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

            _backgroundWorkCancellation.Cancel();

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
            QueueBackgroundWork(
                QueueMutationAsync(
                    baseKey,
                    static (owner, key, token, state) =>
                    {
                        var storeState = (StoreResponseState)state;
                        return owner.StoreResponseAsync(
                            storeState.RequestMethod,
                            storeState.RequestUri,
                            storeState.RequestHeaders,
                            key,
                            storeState.StatusCode,
                            storeState.ResponseHeaders,
                            storeState.Body,
                            token);
                    },
                    _backgroundWorkCancellation.Token,
                    new StoreResponseState(
                        requestMethod,
                        requestUri,
                        requestHeaders,
                        statusCode,
                        responseHeaders,
                        body)),
                "store");
        }

        private async Task StoreResponseAsync(
            HttpMethod requestMethod,
            Uri requestUri,
            HttpHeaders requestHeaders,
            string baseKey,
            int statusCode,
            HttpHeaders responseHeaders,
            SegmentedBuffer body,
            CancellationToken cancellationToken)
        {
            // Detach from the response completion callback before snapshotting the response body.
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested)
                return;

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

                await StorePreparedEntryAsync(baseKey, prepared, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                body?.Dispose();
            }
        }

        private async Task StorePreparedEntryAsync(
            string baseKey,
            PreparedCacheEntry prepared,
            CancellationToken cancellationToken)
        {
            try
            {
                await _policy.Storage
                    .SetAsync(prepared.Entry.Key, prepared.Entry, cancellationToken)
                    .ConfigureAwait(false);
                RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[TurboHTTP][Cache] Store failed: " + ex);
            }
        }

        private Task StorePreparedEntryQueuedAsync(
            string baseKey,
            PreparedCacheEntry prepared,
            CancellationToken cancellationToken)
        {
            return QueueMutationAndWaitAsync(
                baseKey,
                static (owner, key, token, state) =>
                {
                    var queuedState = (PreparedEntryState)state;
                    return owner.StorePreparedEntryCoreAsync(key, queuedState.Prepared, token);
                },
                cancellationToken,
                new PreparedEntryState(prepared));
        }

        private Task ReplaceStoredEntryAsync(
            string baseKey,
            PreparedCacheEntry prepared,
            string existingStorageKey,
            CancellationToken cancellationToken)
        {
            return QueueMutationAndWaitAsync(
                baseKey,
                static (owner, key, token, state) =>
                {
                    var replacementState = (ReplacementState)state;
                    return owner.ReplaceStoredEntryCoreAsync(
                        key,
                        replacementState.Prepared,
                        replacementState.ExistingStorageKey,
                        token);
                },
                cancellationToken,
                new ReplacementState(prepared, existingStorageKey));
        }

        private Task RemoveStoredEntryAsync(
            string baseKey,
            string storageKey,
            CancellationToken cancellationToken)
        {
            return QueueMutationAndWaitAsync(
                baseKey,
                static (owner, key, token, state) =>
                    owner.RemoveStoredEntryCoreAsync(key, (string)state, token),
                cancellationToken,
                storageKey);
        }

        private async Task StorePreparedEntryCoreAsync(
            string baseKey,
            PreparedCacheEntry prepared,
            CancellationToken cancellationToken)
        {
            await _policy.Storage
                .SetAsync(prepared.Entry.Key, prepared.Entry, cancellationToken)
                .ConfigureAwait(false);
            RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);
        }

        private async Task ReplaceStoredEntryCoreAsync(
            string baseKey,
            PreparedCacheEntry prepared,
            string existingStorageKey,
            CancellationToken cancellationToken)
        {
            await StorePreparedEntryCoreAsync(baseKey, prepared, cancellationToken).ConfigureAwait(false);

            if (!string.Equals(existingStorageKey, prepared.Entry.Key, StringComparison.Ordinal))
                await RemoveStoredEntryCoreAsync(baseKey, existingStorageKey, cancellationToken).ConfigureAwait(false);
        }

        private async Task RemoveStoredEntryCoreAsync(
            string baseKey,
            string storageKey,
            CancellationToken cancellationToken)
        {
            await _policy.Storage.RemoveAsync(storageKey, cancellationToken).ConfigureAwait(false);
            UnregisterStoredVariant(baseKey, storageKey);
        }

        private async Task InvalidateBaseKeyCoreAsync(string baseKey, CancellationToken cancellationToken)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal)
            {
                BuildStorageKey(baseKey, string.Empty)
            };

            foreach (var key in TakeStorageKeys(baseKey))
                keys.Add(key);

            foreach (var key in keys)
                await _policy.Storage.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        }

        private Task QueueMutationAndWaitAsync(
            string baseKey,
            Func<CacheInterceptor, string, CancellationToken, object, Task> mutation,
            CancellationToken cancellationToken,
            object state = null)
        {
            return QueueMutationAsync(baseKey, mutation, cancellationToken, state);
        }

        private Task QueueMutationAsync(
            string baseKey,
            Func<CacheInterceptor, string, CancellationToken, object, Task> mutation,
            CancellationToken cancellationToken,
            object state = null)
        {
            if (string.IsNullOrEmpty(baseKey))
                throw new ArgumentException("Base key cannot be null or empty.", nameof(baseKey));
            if (mutation == null)
                throw new ArgumentNullException(nameof(mutation));

            Task queuedTask;
            lock (_pendingMutationLock)
            {
                _pendingMutations.TryGetValue(baseKey, out var previousTask);
                queuedTask = RunQueuedMutationAsync(baseKey, previousTask, mutation, state, cancellationToken);
                _pendingMutations[baseKey] = queuedTask;
            }

            _ = queuedTask.ContinueWith(
                static (task, mutationState) =>
                {
                    var pendingState = (PendingMutationState)mutationState;
                    pendingState.Owner.ClearPendingMutation(pendingState.BaseKey, task);
                },
                new PendingMutationState(this, baseKey),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return queuedTask;
        }

        private async Task AwaitPendingMutationAsync(string baseKey, CancellationToken cancellationToken)
        {
            Task pendingTask;
            lock (_pendingMutationLock)
            {
                _pendingMutations.TryGetValue(baseKey, out pendingTask);
            }

            if (pendingTask == null)
                return;

            if (pendingTask.IsCompleted || !cancellationToken.CanBeCanceled)
            {
                await AwaitIgnoringFaultAsync(pendingTask).ConfigureAwait(false);
                return;
            }

            var cancellationTask = new TaskCompletionSource<object>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                       static state => ((TaskCompletionSource<object>)state).TrySetCanceled(),
                       cancellationTask))
            {
                var completedTask = await Task.WhenAny(pendingTask, cancellationTask.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completedTask, pendingTask))
                    await cancellationTask.Task.ConfigureAwait(false);
            }

            await AwaitIgnoringFaultAsync(pendingTask).ConfigureAwait(false);
        }

        private static async Task AwaitIgnoringFaultAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Background cache mutations are best-effort. Reads should wait for
                // sequencing but must not fail just because a prior store/remove faulted.
            }
        }

        private async Task RunQueuedMutationAsync(
            string baseKey,
            Task previousTask,
            Func<CacheInterceptor, string, CancellationToken, object, Task> mutation,
            object state,
            CancellationToken cancellationToken)
        {
            if (previousTask != null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch
                {
                    // Later mutations must still run so the queue cannot stall on a prior failure.
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await mutation(this, baseKey, cancellationToken, state).ConfigureAwait(false);
        }

        private void ClearPendingMutation(string baseKey, Task completedTask)
        {
            lock (_pendingMutationLock)
            {
                if (_pendingMutations.TryGetValue(baseKey, out var currentTask)
                    && ReferenceEquals(currentTask, completedTask))
                {
                    _pendingMutations.Remove(baseKey);
                }
            }
        }

        private static void QueueBackgroundWork(Task task, string operation)
        {
            _ = task.ContinueWith(
                static (t, state) =>
                {
                    var ex = t.Exception?.GetBaseException();
                    if (ex != null)
                        Debug.WriteLine("[TurboHTTP][Cache] Background " + state + " failed: " + ex);
                },
                operation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
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
            return TransportDispatchHelper.CollectResponseAsync(dispatch, request, context, cancellationToken);
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
