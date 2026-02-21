using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    /// RFC-aware cache middleware with conditional revalidation support.
    /// </summary>
    public sealed partial class CacheMiddleware : IHttpMiddleware, IDisposable
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

            public CacheControlDirectives(
                bool noStore,
                bool noCache,
                bool @private,
                bool @public,
                bool mustRevalidate,
                TimeSpan? sharedMaxAge,
                TimeSpan? maxAge)
            {
                NoStore = noStore;
                NoCache = noCache;
                Private = @private;
                Public = @public;
                MustRevalidate = mustRevalidate;
                SharedMaxAge = sharedMaxAge;
                MaxAge = maxAge;
            }
        }

        public CacheMiddleware(CachePolicy policy = null)
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

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            if (IsUnsafeMethod(request.Method))
                return await HandleUnsafeInvalidationAsync(request, context, next, cancellationToken).ConfigureAwait(false);

            if (!CanCacheMethod(request.Method))
                return await next(request, context, cancellationToken).ConfigureAwait(false);

            if (!_policy.EnableCache)
                return await next(request, context, cancellationToken).ConfigureAwait(false);

            var requestCacheControl = ParseCacheControl(request.Headers.GetValues("Cache-Control"));
            var forceRevalidation = requestCacheControl.NoCache || HasPragmaNoCache(request.Headers);
            var baseKey = BuildBaseKey(request.Method, request.Uri);
            var lookup = await TryLookupAsync(request, baseKey, cancellationToken).ConfigureAwait(false);
            if (lookup.Entry != null)
            {
                if (forceRevalidation)
                {
                    if (_policy.EnableRevalidation && lookup.Entry.CanRevalidate())
                    {
                        return await RevalidateAsync(
                            request,
                            context,
                            next,
                            cancellationToken,
                            baseKey,
                            lookup).ConfigureAwait(false);
                    }
                }
                else if (lookup.Entry.MustRevalidate || lookup.Entry.IsExpired(DateTime.UtcNow))
                {
                    if (_policy.EnableRevalidation && lookup.Entry.CanRevalidate())
                    {
                        return await RevalidateAsync(
                            request,
                            context,
                            next,
                            cancellationToken,
                            baseKey,
                            lookup).ConfigureAwait(false);
                    }

                    await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                    UnregisterStoredVariant(baseKey, lookup.StorageKey);
                }
                else
                {
                    context.RecordEvent("CacheHit", new Dictionary<string, object>
                    {
                        { "key", baseKey }
                    });
                    return CreateResponseFromEntry(lookup.Entry, request, context, "HIT");
                }
            }

            context.RecordEvent("CacheMiss", new Dictionary<string, object>
            {
                { "key", baseKey }
            });

            var upstreamResponse = await next(request, context, cancellationToken).ConfigureAwait(false);

            if (ShouldConsiderForStorage(request, upstreamResponse))
            {
                var prepared = PrepareEntryForStorage(request, upstreamResponse, baseKey);
                if (prepared.Entry != null)
                {
                    await _policy.Storage
                        .SetAsync(prepared.Entry.Key, prepared.Entry, cancellationToken)
                        .ConfigureAwait(false);
                    RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);

                    context.RecordEvent("CacheStore", new Dictionary<string, object>
                    {
                        { "key", prepared.Entry.Key }
                    });
                }
            }

            return upstreamResponse;
        }

        private async Task<UHttpResponse> HandleUnsafeInvalidationAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var response = await next(request, context, cancellationToken).ConfigureAwait(false);

            if (_policy.InvalidateOnUnsafeMethods && !response.IsError && (int)response.StatusCode < 500)
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

            return response;
        }

        private async Task<UHttpResponse> RevalidateAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
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

            var response = await next(conditionalRequest, context, cancellationToken).ConfigureAwait(false);
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

                return response;
            }

            using (response)
            {
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

                    return CreateResponseFromEntry(merged.Entry, request, context, "REVALIDATED");
                }

                // If merge becomes non-cacheable, keep serving cached snapshot for this response only.
                await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
                UnregisterStoredVariant(baseKey, lookup.StorageKey);
                return CreateResponseFromEntry(lookup.Entry, request, context, "REVALIDATED");
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
            var responseHeaders = response.Headers.Clone();
            StripHopByHopHeaders(responseHeaders);

            if (!_policy.AllowSetCookieResponses && responseHeaders.Contains("Set-Cookie"))
                return default;

            var responseCacheControl = ParseCacheControl(responseHeaders.GetValues("Cache-Control"));
            var requestCacheControl = ParseCacheControl(request.Headers.GetValues("Cache-Control"));

            if (!_policy.AllowCacheForAuthorizedRequests
                && request.Headers.Contains("Authorization")
                && !responseCacheControl.Public)
            {
                return default;
            }

            if (responseCacheControl.NoStore || requestCacheControl.NoStore)
                return default;

            if (responseCacheControl.Private && !_policy.AllowPrivateResponses)
                return default;

            if (!TryResolveVary(responseHeaders, out var varyHeaders, out var varyIsWildcard))
                return default;
            if (varyIsWildcard)
                return default;

            if (TryResolveSensitiveVaryFlags(varyHeaders, out var hasCookieVary, out var hasAuthVary))
            {
                if ((hasCookieVary && !_policy.AllowVaryCookie) || (hasAuthVary && !_policy.AllowVaryAuthorization))
                    return default;
            }

            var nowUtc = DateTime.UtcNow;
            var expiresAtUtc = ResolveExpiry(nowUtc, responseCacheControl, responseHeaders, isSharedCache: false);
            var eTag = responseHeaders.Get("ETag");
            var lastModified = responseHeaders.Get("Last-Modified");
            var pragmaNoCache = HasPragmaNoCache(responseHeaders);
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

            var varyKey = BuildVaryKey(request.Headers, varyHeaders);
            var storageKey = BuildStorageKey(baseKey, varyKey);
            var signature = BuildSignature(varyHeaders);
            var bodySnapshot = SnapshotBodyForCache(response.Body);

            var entry = new CacheEntry(
                key: storageKey,
                statusCode: response.StatusCode,
                headers: responseHeaders,
                body: bodySnapshot,
                cachedAtUtc: nowUtc,
                expiresAtUtc: expiresAtUtc,
                eTag: eTag,
                lastModified: lastModified,
                responseUrl: response.Request?.Uri ?? request.Uri,
                varyHeaders: varyHeaders,
                varyKey: varyKey,
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
                mustRevalidate: mustRevalidate);

            return new PreparedCacheEntry(mergedEntry, signature);
        }

        private UHttpResponse CreateResponseFromEntry(CacheEntry entry, UHttpRequest request, RequestContext context, string provenance)
        {
            // Clone for response immutability. Combined with storage-side cloning this is a
            // deliberate v1 tradeoff; can be optimized with ownership transfer later.
            var headers = entry.Headers.Clone();
            var ageSeconds = ComputeCurrentAgeSeconds(entry, DateTime.UtcNow);
            headers.Set("Age", ageSeconds.ToString(CultureInfo.InvariantCulture));
            headers.Set("X-Cache", provenance);

            return new UHttpResponse(
                statusCode: entry.StatusCode,
                headers: headers,
                body: entry.Body,
                elapsedTime: context.Elapsed,
                request: request);
        }

        private static bool ShouldConsiderForStorage(UHttpRequest request, UHttpResponse response)
        {
            return response != null
                && (response.IsSuccessStatusCode || IsRfc9111CacheableByDefault(response.StatusCode))
                && request.Method != HttpMethod.OPTIONS;
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
            byte[] body)
        {
            return new UHttpRequest(
                method,
                uri,
                headers,
                body,
                source.Timeout,
                source.Metadata);
        }
        private static ReadOnlyMemory<byte> SnapshotBodyForCache(ReadOnlyMemory<byte> source)
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
                throw new ObjectDisposedException(nameof(CacheMiddleware));
        }
    }
}
