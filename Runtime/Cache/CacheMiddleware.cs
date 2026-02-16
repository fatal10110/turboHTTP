using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    public sealed class CacheMiddleware : IHttpMiddleware, IDisposable
    {
        private const string EmptyVaryKeyToken = "~";

        private static readonly string[] SensitiveVaryHeaders =
        {
            "authorization",
            "cookie"
        };

        private static readonly string[] RevalidationMergeHeaders =
        {
            "Cache-Control",
            "ETag",
            "Last-Modified",
            "Expires",
            "Date",
            "Vary"
        };

        private readonly CachePolicy _policy;
        private readonly bool _ownsStorage;
        private readonly object _indexLock = new object();
        // Variant index is an in-memory accelerator only. Entries are ref-counted per
        // signature and cleaned up as storage keys are removed or invalidated.
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

            var baseKey = BuildBaseKey(request.Method, request.Uri);
            var lookup = await TryLookupAsync(request, baseKey, cancellationToken).ConfigureAwait(false);
            if (lookup.Entry != null)
            {
                if (lookup.Entry.MustRevalidate || lookup.Entry.IsExpired(DateTime.UtcNow))
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
                await InvalidateUriAsync(request.Uri, cancellationToken).ConfigureAwait(false);
                context.RecordEvent("CacheInvalidated", new Dictionary<string, object>
                {
                    { "uri", NormalizeUri(request.Uri) }
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

                if (ShouldConsiderForStorage(request, response))
                {
                    var prepared = PrepareEntryForStorage(request, response, baseKey);
                    if (prepared.Entry != null)
                    {
                        await _policy.Storage
                            .SetAsync(prepared.Entry.Key, prepared.Entry, cancellationToken)
                            .ConfigureAwait(false);
                        RegisterStoredVariant(baseKey, prepared.Signature, prepared.Entry.Key);
                    }
                }

                return response;
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

                return CreateResponseFromEntry(merged.Entry, request, context, "REVALIDATED");
            }

            // If merge becomes non-cacheable, keep serving cached snapshot for this response only.
            await _policy.Storage.RemoveAsync(lookup.StorageKey, cancellationToken).ConfigureAwait(false);
            UnregisterStoredVariant(baseKey, lookup.StorageKey);
            return CreateResponseFromEntry(lookup.Entry, request, context, "REVALIDATED");
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
            if (!_policy.AllowSetCookieResponses && response.Headers.Contains("Set-Cookie"))
                return default;

            var responseCacheControl = ParseCacheControl(response.Headers.GetValues("Cache-Control"));
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

            if (!TryResolveVary(response.Headers, out var varyHeaders, out var varyIsWildcard))
                return default;
            if (varyIsWildcard)
                return default;

            var containsSensitiveVary = varyHeaders.Any(h => IsSensitiveVaryHeader(h));
            if (containsSensitiveVary)
            {
                var hasCookieVary = varyHeaders.Any(h => string.Equals(h, "cookie", StringComparison.OrdinalIgnoreCase));
                var hasAuthVary = varyHeaders.Any(h => string.Equals(h, "authorization", StringComparison.OrdinalIgnoreCase));
                if ((hasCookieVary && !_policy.AllowVaryCookie) || (hasAuthVary && !_policy.AllowVaryAuthorization))
                    return default;
            }

            var nowUtc = DateTime.UtcNow;
            var expiresAtUtc = ResolveExpiry(nowUtc, responseCacheControl, response.Headers);
            var eTag = response.Headers.Get("ETag");
            var lastModified = response.Headers.Get("Last-Modified");
            var pragmaNoCache = HasPragmaNoCache(response.Headers);
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
                headers: response.Headers,
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
            for (int i = 0; i < RevalidationMergeHeaders.Length; i++)
            {
                var name = RevalidationMergeHeaders[i];
                if (!notModifiedResponse.Headers.Contains(name))
                    continue;

                mergedHeaders.Remove(name);
                var values = notModifiedResponse.Headers.GetValues(name);
                for (int j = 0; j < values.Count; j++)
                    mergedHeaders.Add(name, values[j]);
            }

            if (!TryResolveVary(mergedHeaders, out var varyHeaders, out var varyIsWildcard))
                return default;
            if (varyIsWildcard)
                return default;

            var containsSensitiveVary = varyHeaders.Any(h => IsSensitiveVaryHeader(h));
            if (containsSensitiveVary)
            {
                var hasCookieVary = varyHeaders.Any(h => string.Equals(h, "cookie", StringComparison.OrdinalIgnoreCase));
                var hasAuthVary = varyHeaders.Any(h => string.Equals(h, "authorization", StringComparison.OrdinalIgnoreCase));
                if ((hasCookieVary && !_policy.AllowVaryCookie) || (hasAuthVary && !_policy.AllowVaryAuthorization))
                    return default;
            }

            var nowUtc = DateTime.UtcNow;
            var cacheControl = ParseCacheControl(mergedHeaders.GetValues("Cache-Control"));
            var expiresAtUtc = ResolveExpiry(nowUtc, cacheControl, mergedHeaders);
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
            var headers = entry.Headers.Clone();
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

        private static CacheControlDirectives ParseCacheControl(IReadOnlyList<string> values)
        {
            bool noStore = false;
            bool noCache = false;
            bool @private = false;
            bool @public = false;
            bool mustRevalidate = false;
            TimeSpan? sharedMaxAge = null;
            TimeSpan? maxAge = null;

            if (values != null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    var value = values[i];
                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var directives = value.Split(',');
                    for (int j = 0; j < directives.Length; j++)
                    {
                        var directive = directives[j].Trim();
                        if (directive.Length == 0)
                            continue;

                        if (string.Equals(directive, "no-store", StringComparison.OrdinalIgnoreCase))
                        {
                            noStore = true;
                            continue;
                        }

                        if (string.Equals(directive, "no-cache", StringComparison.OrdinalIgnoreCase))
                        {
                            noCache = true;
                            continue;
                        }

                        if (string.Equals(directive, "private", StringComparison.OrdinalIgnoreCase))
                        {
                            @private = true;
                            continue;
                        }

                        if (string.Equals(directive, "public", StringComparison.OrdinalIgnoreCase))
                        {
                            @public = true;
                            continue;
                        }

                        if (string.Equals(directive, "must-revalidate", StringComparison.OrdinalIgnoreCase))
                        {
                            mustRevalidate = true;
                            continue;
                        }

                        if (directive.StartsWith("s-maxage=", StringComparison.OrdinalIgnoreCase))
                        {
                            var sharedSecondsPart = directive.Substring("s-maxage=".Length).Trim().Trim('"');
                            if (!long.TryParse(sharedSecondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out var sharedSeconds))
                                continue;

                            if (sharedSeconds < 0)
                                continue;

                            sharedMaxAge = TimeSpan.FromSeconds(sharedSeconds);
                            continue;
                        }

                        if (directive.StartsWith("max-age=", StringComparison.OrdinalIgnoreCase))
                        {
                            var secondsPart = directive.Substring("max-age=".Length).Trim().Trim('"');
                            if (!long.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                                continue;

                            if (seconds < 0)
                                continue;

                            maxAge = TimeSpan.FromSeconds(seconds);
                            continue;
                        }
                    }
                }
            }

            return new CacheControlDirectives(noStore, noCache, @private, @public, mustRevalidate, sharedMaxAge, maxAge);
        }

        private static DateTime? ResolveExpiry(DateTime nowUtc, CacheControlDirectives cacheControl, HttpHeaders headers)
        {
            // RFC precedence: s-maxage overrides max-age, both override Expires.
            if (cacheControl.SharedMaxAge.HasValue)
                return nowUtc.Add(cacheControl.SharedMaxAge.Value);

            if (cacheControl.MaxAge.HasValue)
                return nowUtc.Add(cacheControl.MaxAge.Value);

            var expiresValue = headers.Get("Expires");
            if (string.IsNullOrWhiteSpace(expiresValue))
                return null;

            expiresValue = expiresValue.Trim();
            if (expiresValue == "0" || expiresValue == "-1")
                return nowUtc.AddSeconds(-1);

            if (DateTime.TryParse(
                expiresValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static bool HasPragmaNoCache(HttpHeaders headers)
        {
            var values = headers.GetValues("Pragma");
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] != null && values[i].IndexOf("no-cache", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static bool TryResolveVary(HttpHeaders headers, out string[] varyHeaders, out bool wildcard)
        {
            wildcard = false;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var values = headers.GetValues("Vary");

            for (int i = 0; i < values.Count; i++)
            {
                var line = values[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var parts = line.Split(',');
                for (int j = 0; j < parts.Length; j++)
                {
                    var token = parts[j].Trim();
                    if (token.Length == 0)
                        continue;

                    if (token == "*")
                    {
                        wildcard = true;
                        varyHeaders = Array.Empty<string>();
                        return true;
                    }

                    set.Add(token.ToLowerInvariant());
                }
            }

            varyHeaders = set.OrderBy(h => h, StringComparer.Ordinal).ToArray();
            return true;
        }

        private static string BuildVaryKey(HttpHeaders requestHeaders, IReadOnlyList<string> varyHeaders)
        {
            if (varyHeaders == null || varyHeaders.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(varyHeaders.Count * 32);
            for (int i = 0; i < varyHeaders.Count; i++)
            {
                var name = varyHeaders[i].ToLowerInvariant();
                var values = requestHeaders.GetValues(name);

                sb.Append(name);
                sb.Append('=');

                if (values.Count == 0)
                {
                    sb.Append(EmptyVaryKeyToken);
                }
                else
                {
                    for (int j = 0; j < values.Count; j++)
                    {
                        if (j > 0)
                            sb.Append(',');

                        AppendVaryValueToken(sb, values[j]);
                    }
                }

                sb.Append(';');
            }

            return sb.ToString();
        }

        private static string BuildSignature(IReadOnlyList<string> varyHeaders)
        {
            if (varyHeaders == null || varyHeaders.Count == 0)
                return string.Empty;

            return string.Join("\n", varyHeaders);
        }

        private static string[] ParseSignature(string signature)
        {
            if (string.IsNullOrEmpty(signature))
                return Array.Empty<string>();

            return signature.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string BuildBaseKey(HttpMethod method, Uri uri)
        {
            return method.ToUpperString() + " " + NormalizeUri(uri);
        }

        private static string BuildStorageKey(string baseKey, string varyKey)
        {
            return baseKey + "|" + (string.IsNullOrEmpty(varyKey) ? EmptyVaryKeyToken : varyKey);
        }

        private string[] GetSignatureSnapshot(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket) || bucket.Signatures.Count == 0)
                    return Array.Empty<string>();

                return bucket.Signatures.OrderBy(s => s, StringComparer.Ordinal).ToArray();
            }
        }

        private void RegisterStoredVariant(string baseKey, string signature, string storageKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                {
                    bucket = new VariantBucket();
                    _variantIndex[baseKey] = bucket;
                }

                var normalizedSignature = signature ?? string.Empty;
                if (bucket.SignatureByStorageKey.TryGetValue(storageKey, out var previousSignature))
                {
                    if (string.Equals(previousSignature, normalizedSignature, StringComparison.Ordinal))
                    {
                        bucket.StorageKeys.Add(storageKey);
                        bucket.Signatures.Add(normalizedSignature);
                        return;
                    }

                    ReleaseSignatureRefUnsafe(bucket, previousSignature);
                }

                bucket.SignatureByStorageKey[storageKey] = normalizedSignature;
                AddSignatureRefUnsafe(bucket, normalizedSignature);
                bucket.StorageKeys.Add(storageKey);
            }
        }

        private void UnregisterStoredVariant(string baseKey, string storageKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                    return;

                if (!bucket.StorageKeys.Remove(storageKey))
                    return;

                if (bucket.SignatureByStorageKey.TryGetValue(storageKey, out var signature))
                {
                    bucket.SignatureByStorageKey.Remove(storageKey);
                    ReleaseSignatureRefUnsafe(bucket, signature);
                }

                if (bucket.StorageKeys.Count == 0)
                    _variantIndex.Remove(baseKey);
            }
        }

        private string[] TakeStorageKeys(string baseKey)
        {
            lock (_indexLock)
            {
                if (!_variantIndex.TryGetValue(baseKey, out var bucket))
                    return Array.Empty<string>();

                _variantIndex.Remove(baseKey);
                return bucket.StorageKeys.ToArray();
            }
        }

        internal static string NormalizeUri(Uri uri)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("Cache key URI must be absolute.", nameof(uri));

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host.ToLowerInvariant();
            var portPart = IsDefaultPort(scheme, uri.Port) ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            var path = NormalizePath(uri.AbsolutePath);
            var query = NormalizeQuery(uri.Query);

            return scheme + "://" + host + portPart + path + query;
        }

        private static bool IsDefaultPort(string scheme, int port)
        {
            return (scheme == "http" && port == 80)
                   || (scheme == "https" && port == 443);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "/";

            var hadTrailingSlash = path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal);
            var segments = path.Split('/');
            var stack = new List<string>(segments.Length);

            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0 || segment == ".")
                    continue;

                if (segment == "..")
                {
                    if (stack.Count > 0)
                        stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                stack.Add(NormalizePercentEncoding(segment));
            }

            var normalized = "/" + string.Join("/", stack);
            if (normalized.Length == 0)
                normalized = "/";

            if (hadTrailingSlash && normalized != "/")
                normalized += "/";

            return normalized;
        }

        private static string NormalizeQuery(string query)
        {
            if (string.IsNullOrEmpty(query) || query == "?")
                return string.Empty;

            var raw = query[0] == '?' ? query.Substring(1) : query;
            if (raw.Length == 0)
                return string.Empty;

            var items = new List<QueryPart>();
            var segments = raw.Split('&');
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (segment.Length == 0)
                    continue;

                int eq = segment.IndexOf('=');
                if (eq < 0)
                {
                    items.Add(new QueryPart(NormalizePercentEncoding(segment), string.Empty, false, items.Count));
                    continue;
                }

                var name = segment.Substring(0, eq);
                var value = segment.Substring(eq + 1);
                items.Add(new QueryPart(
                    NormalizePercentEncoding(name),
                    NormalizePercentEncoding(value),
                    hasEquals: true,
                    ordinal: items.Count));
            }

            items.Sort((a, b) =>
            {
                int byName = string.CompareOrdinal(a.Name, b.Name);
                if (byName != 0)
                    return byName;

                // Preserve duplicate key order to avoid changing semantics for repeated parameters.
                return a.Ordinal.CompareTo(b.Ordinal);
            });

            if (items.Count == 0)
                return string.Empty;

            var sb = new StringBuilder(raw.Length + 1);
            sb.Append('?');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0)
                    sb.Append('&');

                sb.Append(items[i].Name);
                if (items[i].HasEquals)
                {
                    sb.Append('=');
                    sb.Append(items[i].Value);
                }
            }

            return sb.ToString();
        }

        private static string NormalizePercentEncoding(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (ch == '%' && i + 2 < input.Length
                    && IsHex(input[i + 1])
                    && IsHex(input[i + 2]))
                {
                    var high = HexToInt(input[i + 1]);
                    var low = HexToInt(input[i + 2]);
                    var value = (char)((high << 4) + low);

                    if (IsUnreserved(value))
                    {
                        sb.Append(value);
                    }
                    else
                    {
                        sb.Append('%');
                        sb.Append(char.ToUpperInvariant(input[i + 1]));
                        sb.Append(char.ToUpperInvariant(input[i + 2]));
                    }

                    i += 2;
                    continue;
                }

                sb.Append(ch);
            }

            return sb.ToString();
        }

        private static bool IsUnreserved(char c)
        {
            return (c >= 'A' && c <= 'Z')
                   || (c >= 'a' && c <= 'z')
                   || (c >= '0' && c <= '9')
                   || c == '-'
                   || c == '.'
                   || c == '_'
                   || c == '~';
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9')
                   || (c >= 'a' && c <= 'f')
                   || (c >= 'A' && c <= 'F');
        }

        private static int HexToInt(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return 10 + (c - 'a');
            return 10 + (c - 'A');
        }

        private static bool IsSensitiveVaryHeader(string headerName)
        {
            for (int i = 0; i < SensitiveVaryHeaders.Length; i++)
            {
                if (string.Equals(SensitiveVaryHeaders[i], headerName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsRfc9111CacheableByDefault(HttpStatusCode statusCode)
        {
            var code = (int)statusCode;
            return code == 300
                   || code == 301
                   || code == 308
                   || code == 404
                   || code == 405
                   || code == 410
                   || code == 414
                   || code == 501;
        }

        private static void AddSignatureRefUnsafe(VariantBucket bucket, string signature)
        {
            bucket.Signatures.Add(signature);

            if (!bucket.SignatureRefCounts.TryGetValue(signature, out var count))
                count = 0;

            bucket.SignatureRefCounts[signature] = count + 1;
        }

        private static void ReleaseSignatureRefUnsafe(VariantBucket bucket, string signature)
        {
            if (!bucket.SignatureRefCounts.TryGetValue(signature, out var count))
            {
                bucket.Signatures.Remove(signature);
                return;
            }

            if (count <= 1)
            {
                bucket.SignatureRefCounts.Remove(signature);
                bucket.Signatures.Remove(signature);
                return;
            }

            bucket.SignatureRefCounts[signature] = count - 1;
        }

        private static void AppendVaryValueToken(StringBuilder sb, string rawValue)
        {
            var value = (rawValue ?? string.Empty).Trim();
            sb.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(value);
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

        private readonly struct QueryPart
        {
            public readonly string Name;
            public readonly string Value;
            public readonly bool HasEquals;
            public readonly int Ordinal;

            public QueryPart(string name, string value, bool hasEquals, int ordinal)
            {
                Name = name;
                Value = value;
                HasEquals = hasEquals;
                Ordinal = ordinal;
            }
        }
    }
}
