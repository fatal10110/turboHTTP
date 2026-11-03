using System;
using System.Buffers;
using System.Collections.Generic;
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
        private long _maxCacheableResponseBodyBytes = 8L * 1024 * 1024;

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
        /// <summary> Gets or sets the maximum response body size retained for cache writes. Default 8 MiB. </summary>
        public long MaxCacheableResponseBodyBytes
        {
            get => _maxCacheableResponseBodyBytes;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Must be > 0.");

                _maxCacheableResponseBodyBytes = value;
            }
        }
        /// <summary> Gets or sets the storage backend. Defaults to in-memory storage. </summary>
        public ICacheStorage Storage { get; set; } = new MemoryCacheStorage();
    }

    /// <summary>
    /// RFC-aware cache interceptor with conditional revalidation support.
    /// </summary>
    public sealed partial class CacheInterceptor : IHttpInterceptor, IDisposable, IInterceptorRole
    {
        // Pipeline ordering role, checked by UHttpClient (#55).
        InterceptorRole IInterceptorRole.Role => InterceptorRole.Cache;

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

        // RFC 9111 section 3.2: fields a 304 never writes into the stored response. Content-Length is never updated;
        // Content-Encoding describes the processing the stored (decoded) body went through; Content-Range is
        // processed and removed by the recipient; Transfer-Encoding is hop-by-hop framing.
        private static readonly string[] NotModifiedMergeExcludedHeaders =
        {
            "Content-Length",
            "Content-Encoding",
            "Content-Range",
            "Transfer-Encoding"
        };

        private readonly CachePolicy _policy;
        private readonly bool _ownsStorage;
        private readonly object _indexLock = new object();
        // Variant index is an in-memory accelerator only. Entries are ref-counted per
        // signature and cleaned up as storage keys are removed or invalidated.
        // This index is intentionally not persisted across process restarts in v1.
        // Storage evicts silently, so the index is also bounded LRU-style by
        // _variantIndexCapacity (#60); _variantLru orders the buckets (head = least recent).
        // All three are guarded by _indexLock.
        private readonly Dictionary<string, VariantBucket> _variantIndex = new Dictionary<string, VariantBucket>(StringComparer.Ordinal);
        private readonly LinkedList<string> _variantLru = new LinkedList<string>();
        private int _variantIndexCapacity;
        private readonly object _pendingMutationLock = new object();
        private readonly Dictionary<string, Task> _pendingMutations = new Dictionary<string, Task>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _backgroundWorkCancellation = new CancellationTokenSource();
        // Captured once: CancellationTokenSource.Token throws after Dispose, the captured token does not.
        private readonly CancellationToken _backgroundWorkToken;
        private Func<IStreamingCacheAccumulator> _streamingAccumulatorFactory =
            static () => new SegmentedBufferStreamingCacheAccumulator();
        // Clock for freshness and age (lookup, store, 304 merge, served Age). Replaced only by tests.
        private Func<DateTime> _utcNow = static () => DateTime.UtcNow;
        private int _disposed;

        private sealed class VariantBucket
        {
            public readonly HashSet<string> Signatures = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> StorageKeys = new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string, string> SignatureByStorageKey = new Dictionary<string, string>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> SignatureRefCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            public LinkedListNode<string> LruNode;
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

        // Owns the pooled body until StoreResponseAsync takes it over; disposed by
        // RunQueuedMutationAsync when the store is skipped (#71).
        private sealed class StoreResponseState : IDisposable
        {
            public readonly HttpMethod RequestMethod;
            public readonly Uri RequestUri;
            public readonly HttpHeaders RequestHeaders;
            public readonly int StatusCode;
            public readonly HttpHeaders ResponseHeaders;
            public readonly HttpHeaders Trailers;
            public readonly DateTime? ResponseTimeUtc;
            public readonly SegmentedBuffer Body;

            public StoreResponseState(
                HttpMethod requestMethod,
                Uri requestUri,
                HttpHeaders requestHeaders,
                int statusCode,
                HttpHeaders responseHeaders,
                HttpHeaders trailers,
                DateTime? responseTimeUtc,
                SegmentedBuffer body)
            {
                RequestMethod = requestMethod;
                RequestUri = requestUri;
                RequestHeaders = requestHeaders;
                StatusCode = statusCode;
                ResponseHeaders = responseHeaders;
                Trailers = trailers;
                ResponseTimeUtc = responseTimeUtc;
                Body = body;
            }

            public void Dispose()
            {
                Body?.Dispose();
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

            _backgroundWorkToken = _backgroundWorkCancellation.Token;
            // Every live bucket holds at least one storage entry, so the storage entry limit bounds
            // the live index; storages with no known limit get a fixed cap.
            _variantIndexCapacity = _policy.Storage is MemoryCacheStorage memoryStorage
                ? memoryStorage.MaxEntries
                : DefaultVariantIndexCapacity;
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

                // RFC 9111 section 3.3: this cache does not store partial content, so a Range/If-Range request
                // is neither answered from a stored response nor allowed to store its (possibly 206) response.
                if (!CanCacheMethod(request.Method) || !_policy.EnableCache || IsRangeRequest(request.Headers))
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
                    var nowUtc = _utcNow();
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
                            await ServeCachedEntryAsync(handler, lookup.Entry, request, context, "HIT").ConfigureAwait(false);
                            return;
                        }

                        if (_policy.EnableRevalidation && lookup.Entry.IsStaleWhileRevalidate(nowUtc))
                        {
                            context.RecordEvent("CacheHit", new Dictionary<string, object>
                            {
                                { "key", baseKey }
                            });
                            await ServeCachedEntryAsync(handler, lookup.Entry, request, context, "STALE").ConfigureAwait(false);

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

        private Task HandleUnsafeInvalidationAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            DispatchFunc next,
            CancellationToken cancellationToken)
        {
            if (!_policy.InvalidateOnUnsafeMethods)
                return next(request, handler, context, cancellationToken);

            // The response streams through untouched; invalidation runs when the status is known.
            return next(
                request,
                new UnsafeInvalidationHandler(this, handler, request.Uri, cancellationToken),
                context,
                cancellationToken);
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

            var conditionalRequest = request
                .CopyWithSharedContent()
                .WithHeaders(conditionalHeaders);

            // The validation response is not buffered: RevalidationHandler sees the status first, serves the
            // merged stored entry for 304 and streams any other response to the caller (teeing it into the
            // store when storable). StreamingResponseRequested is left as the caller set it.
            var revalidationHandler = new RevalidationHandler(this, handler, request, baseKey, lookup, cancellationToken);
            context.UpdateRequest(conditionalRequest);
            try
            {
                await next(conditionalRequest, revalidationHandler, context, cancellationToken).ConfigureAwait(false);
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
                    _backgroundWorkToken),
                "revalidation");
        }

        private async Task RunBackgroundRevalidationAsync(
            UHttpRequest request,
            DispatchFunc next,
            string baseKey,
            CacheLookupResult lookup,
            CancellationToken cancellationToken)
        {
            // Detach from the foreground cache-hit path onto the thread pool. Task.Yield would resume
            // on the caller's SynchronizationContext (Unity main thread), which may not be pumping (#245).
            await new ThreadPoolHop();

            RequestContext context = null;
            try
            {
                // Checked inside the try so the cloned request is disposed on cancel-before-start too.
                if (cancellationToken.IsCancellationRequested)
                    return;

                context = RequestContext.CreateForBackground(request);
                await RevalidateAsync(
                    request,
                    new BackgroundRevalidationSink(MaxCacheableResponseBodyBytes, cancellationToken),
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
                TurboDiagnostics.Log("[TurboHTTP][Cache] Background revalidation failed", ex);
            }
            finally
            {
                context?.Stop();
                context?.Clear();
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
                response.Trailers,
                responseTimeUtc: null,
                response.Request?.Uri ?? request.Uri,
                baseKey);
        }

        private PreparedCacheEntry PrepareEntryForStorage(
            Uri requestUri,
            HttpHeaders requestHeaders,
            HttpStatusCode statusCode,
            HttpHeaders responseHeaders,
            ReadOnlyMemory<byte> bodySnapshot,
            HttpHeaders trailers,
            DateTime? responseTimeUtc,
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

            // response_time (RFC 9111 section 4.2.3): when the response arrived, not when this background store runs.
            var nowUtc = responseTimeUtc ?? _utcNow();
            EnsureDateHeader(responseHeadersForStorage, nowUtc);
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
                mustRevalidate: mustRevalidate,
                trailers: trailers);

            return new PreparedCacheEntry(entry, signature);
        }

        private PreparedCacheEntry MergeNotModifiedEntry(
            UHttpRequest request,
            CacheEntry existing,
            HttpHeaders notModifiedHeaders,
            string baseKey)
        {
            var mergedHeaders = existing.Headers.Clone();
            // The stored Age and Date describe the old response; the 304 is the new response (RFC 9111 section 4.2.3:
            // age is counted from the last validation). Keep only the 304's own values, or the receive time as Date.
            mergedHeaders.Remove("Age");
            mergedHeaders.Remove("Date");
            foreach (var name in notModifiedHeaders.Names)
            {
                if (!notModifiedHeaders.Contains(name))
                    continue;

                // RFC 9111 section 3.2. The stored body is whatever the inner pipeline produced (e.g. decoded by
                // Decompression), so a 304 repeating Content-Encoding must not relabel it. Content-Type is updated:
                // the stored bytes are not parsed by the cache.
                if (IsNotModifiedMergeExcluded(name))
                    continue;

                // A cookie the policy keeps out of storage stays out when a 304 carries it; the revalidating
                // consumer still receives it (ServeCachedEntryAsync), later hits never replay it.
                if (!_policy.AllowSetCookieResponses && string.Equals(name, "Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    continue;

                mergedHeaders.Remove(name);
                var values = notModifiedHeaders.GetValues(name);
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

            var nowUtc = _utcNow();
            EnsureDateHeader(mergedHeaders, nowUtc);
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
                mustRevalidate: mustRevalidate,
                trailers: existing.Trailers);

            return new PreparedCacheEntry(mergedEntry, signature);
        }

        private async Task ServeCachedEntryAsync(
            IHttpHandler handler,
            CacheEntry entry,
            UHttpRequest request,
            RequestContext context,
            string provenance,
            IReadOnlyList<string> responseSetCookies = null)
        {
            var headers = entry.Headers.Clone();
            var ageSeconds = ComputeCurrentAgeSeconds(entry, _utcNow());
            headers.Set("Age", ageSeconds.ToString(CultureInfo.InvariantCulture));
            headers.Set("X-Cache", provenance);

            // A revalidating 304's own Set-Cookie belongs to this response only (never merged into storage when the
            // policy refuses Set-Cookie responses).
            if (responseSetCookies != null && responseSetCookies.Count > 0)
            {
                headers.Remove("Set-Cookie");
                for (int i = 0; i < responseSetCookies.Count; i++)
                    headers.Add("Set-Cookie", responseSetCookies[i]);
            }

            // Cache hits synthesize the same handler lifecycle as a network response.
            // OnRequestStart here means "response delivery begins", not "socket dispatch started".
            handler.OnRequestStart(request, context);
            await handler.OnResponseStartAsync(
                (int)entry.StatusCode,
                headers,
                new BufferedResponseBodySource(entry.Body, entry.Trailers),
                context).ConfigureAwait(false);
        }

        private static bool ShouldConsiderForStorage(HttpMethod requestMethod, HttpStatusCode responseStatusCode)
        {
            var numericStatusCode = (int)responseStatusCode;

            // RFC 9111 section 3.3: a cache that does not implement partial-content storage MUST NOT store
            // a 206 response.
            if (numericStatusCode == 206)
                return false;

            return ((numericStatusCode >= 200 && numericStatusCode < 300)
                    || IsRfc9111CacheableByDefault(responseStatusCode))
                   && requestMethod != HttpMethod.OPTIONS;
        }

        private static bool IsRangeRequest(HttpHeaders headers)
        {
            return headers != null && (headers.Contains("Range") || headers.Contains("If-Range"));
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

            try
            {
                _backgroundWorkCancellation.Cancel();
            }
            finally
            {
                // Background work holds _backgroundWorkToken, which stays readable (and cancelled).
                _backgroundWorkCancellation.Dispose();
            }

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
            SegmentedBuffer body,
            HttpHeaders trailers = null,
            DateTime? responseTimeUtc = null)
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
                            storeState.Trailers,
                            storeState.ResponseTimeUtc,
                            storeState.Body,
                            token);
                    },
                    _backgroundWorkToken,
                    new StoreResponseState(
                        requestMethod,
                        requestUri,
                        requestHeaders,
                        statusCode,
                        responseHeaders,
                        trailers,
                        responseTimeUtc,
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
            HttpHeaders trailers,
            DateTime? responseTimeUtc,
            SegmentedBuffer body,
            CancellationToken cancellationToken)
        {
            // Detach from the response completion callback (and from _pendingMutationLock, which
            // QueueMutationAsync holds while it starts this mutation inline) onto the thread pool
            // before snapshotting the body. Task.Yield would resume on the caller's
            // SynchronizationContext, and a foreground lookup of this key waits on this task (#245).
            await new ThreadPoolHop();

            try
            {
                // Checked inside the try so the pooled body is returned on cancel-before-start too.
                if (cancellationToken.IsCancellationRequested)
                    return;

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
                    trailers,
                    responseTimeUtc,
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
                TurboDiagnostics.Log("[TurboHTTP][Cache] Store failed", ex);
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

            if (cancellationToken.IsCancellationRequested)
            {
                // The mutation never runs, so release what its state owns (the pooled store body, #71).
                (state as IDisposable)?.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }

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
                        TurboDiagnostics.Log("[TurboHTTP][Cache] Background " + state + " failed", ex);
                },
                operation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        internal long MaxCacheableResponseBodyBytes => _policy.MaxCacheableResponseBodyBytes;

        /// <summary>Background token; stays cancelled (not throwing) after <see cref="Dispose"/>. For tests.</summary>
        internal CancellationToken BackgroundWorkToken => _backgroundWorkToken;

        internal int PendingMutationCount
        {
            get
            {
                lock (_pendingMutationLock)
                {
                    return _pendingMutations.Count;
                }
            }
        }

        internal Func<IStreamingCacheAccumulator> StreamingAccumulatorFactory
        {
            get => _streamingAccumulatorFactory;
            set => _streamingAccumulatorFactory = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>Test seam: the clock used for freshness, age and the Date appended to stored responses.</summary>
        internal Func<DateTime> UtcNowProvider
        {
            get => _utcNow;
            set => _utcNow = value ?? throw new ArgumentNullException(nameof(value));
        }

        internal DateTime GetUtcNow()
        {
            return _utcNow();
        }

        internal IStreamingCacheAccumulator CreateStreamingAccumulator()
        {
            return _streamingAccumulatorFactory();
        }

        internal static bool ShouldConsiderForStorage(HttpMethod requestMethod, int statusCode)
        {
            return statusCode > 0 && ShouldConsiderForStorage(requestMethod, (HttpStatusCode)statusCode);
        }
    }
}
