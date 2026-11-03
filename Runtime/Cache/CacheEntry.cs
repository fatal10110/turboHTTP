using System;
using System.Collections.Generic;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Immutable snapshot of a cached HTTP response variant.
    /// </summary>
    public sealed class CacheEntry
    {
        /// <summary> Gets the storage key. </summary>
        public string Key { get; }
        /// <summary> Gets the response status code. </summary>
        public HttpStatusCode StatusCode { get; }
        /// <summary> Gets the response headers. </summary>
        public HttpHeaders Headers { get; }
        /// <summary> Gets the response body. </summary>
        public ReadOnlyMemory<byte> Body { get; }
        /// <summary> Gets the UTC time when this entry was cached. </summary>
        public DateTime CachedAtUtc { get; }
        /// <summary> Gets the UTC time when this entry expires, or null if it does not expire or requires revalidation. </summary>
        public DateTime? ExpiresAtUtc { get; }
        /// <summary> Gets the ETag for conditional requests. </summary>
        public string ETag { get; }
        /// <summary> Gets the Last-Modified date for conditional requests. </summary>
        public string LastModified { get; }
        /// <summary> Gets the URL of the response. </summary>
        public Uri ResponseUrl { get; }
        /// <summary> Gets the list of Vary headers used to discriminate this variant. </summary>
        public IReadOnlyList<string> VaryHeaders { get; }
        /// <summary> Gets the normalized Vary key signature. </summary>
        public string VaryKey { get; }
        /// <summary> Gets the duration this entry can be served stale while revalidating in the background. </summary>
        public TimeSpan? StaleWhileRevalidate { get; }
        /// <summary> Gets a value indicating whether this entry must be revalidated with the origin. </summary>
        public bool MustRevalidate { get; }
        /// <summary>
        /// Gets the response trailer fields, kept apart from <see cref="Headers"/> (RFC 9111 section 3.1) and replayed on
        /// cache hits. Empty when the response had none. A custom <see cref="ICacheStorage"/> that rebuilds entries field
        /// by field must copy this property too, or replayed responses lose their trailers.
        /// </summary>
        public HttpHeaders Trailers { get; }

        /// <summary> Gets the length of the response body. </summary>
        public int BodyLength => Body.Length;

        /// <summary> Initializes a new instance of the <see cref="CacheEntry"/> class without trailer fields. </summary>
        public CacheEntry(
            string key,
            HttpStatusCode statusCode,
            HttpHeaders headers,
            ReadOnlyMemory<byte> body,
            DateTime cachedAtUtc,
            DateTime? expiresAtUtc,
            string eTag,
            string lastModified,
            Uri responseUrl,
            IReadOnlyList<string> varyHeaders,
            string varyKey,
            TimeSpan? staleWhileRevalidate,
            bool mustRevalidate)
            : this(
                key,
                statusCode,
                headers,
                body,
                cachedAtUtc,
                expiresAtUtc,
                eTag,
                lastModified,
                responseUrl,
                varyHeaders,
                varyKey,
                staleWhileRevalidate,
                mustRevalidate,
                trailers: null)
        {
        }

        /// <summary> Initializes a new instance of the <see cref="CacheEntry"/> class. </summary>
        /// <param name="trailers">The response trailer fields to replay on hits, or null for none. The entry keeps a copy.</param>
        public CacheEntry(
            string key,
            HttpStatusCode statusCode,
            HttpHeaders headers,
            ReadOnlyMemory<byte> body,
            DateTime cachedAtUtc,
            DateTime? expiresAtUtc,
            string eTag,
            string lastModified,
            Uri responseUrl,
            IReadOnlyList<string> varyHeaders,
            string varyKey,
            TimeSpan? staleWhileRevalidate,
            bool mustRevalidate,
            HttpHeaders trailers)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            StatusCode = statusCode;
            Headers = headers?.Clone() ?? new HttpHeaders();
            Body = body;
            CachedAtUtc = EnsureUtc(cachedAtUtc);
            ExpiresAtUtc = expiresAtUtc.HasValue ? EnsureUtc(expiresAtUtc.Value) : (DateTime?)null;
            ETag = eTag;
            LastModified = lastModified;
            ResponseUrl = responseUrl;
            VaryHeaders = NormalizeVaryHeaders(varyHeaders);
            VaryKey = varyKey ?? string.Empty;
            StaleWhileRevalidate = staleWhileRevalidate;
            MustRevalidate = mustRevalidate;
            Trailers = trailers == null || trailers.Count == 0 ? HttpHeaders.Empty : trailers.Clone();
        }

        /// <summary>
        /// Returns true when the entry can be served without foreground revalidation.
        /// </summary>
        public bool IsFresh(DateTime utcNow)
        {
            return !MustRevalidate && !IsExpired(utcNow);
        }

        /// <summary>
        /// Returns true when the entry freshness lifetime has elapsed.
        /// </summary>
        public bool IsExpired()
        {
            return IsExpired(DateTime.UtcNow);
        }

        /// <summary>
        /// Returns true when <paramref name="utcNow"/> is on/after <see cref="ExpiresAtUtc"/>.
        /// </summary>
        public bool IsExpired(DateTime utcNow)
        {
            if (!ExpiresAtUtc.HasValue)
                return false;

            return EnsureUtc(utcNow) >= ExpiresAtUtc.Value;
        }

        /// <summary>
        /// Returns true if this entry has validator headers suitable for conditional requests.
        /// </summary>
        public bool CanRevalidate()
        {
            return !string.IsNullOrEmpty(ETag) || !string.IsNullOrEmpty(LastModified);
        }

        /// <summary>
        /// Returns true when the entry is stale but still inside the stale-while-revalidate window.
        /// </summary>
        public bool IsStaleWhileRevalidate(DateTime utcNow)
        {
            if (MustRevalidate || !ExpiresAtUtc.HasValue || !StaleWhileRevalidate.HasValue || !CanRevalidate())
                return false;

            var now = EnsureUtc(utcNow);
            return now >= ExpiresAtUtc.Value
                   && now < ExpiresAtUtc.Value + StaleWhileRevalidate.Value;
        }

        internal bool ShouldEvict(DateTime utcNow)
        {
            if (!ExpiresAtUtc.HasValue)
                return false;

            var now = EnsureUtc(utcNow);
            if (now < ExpiresAtUtc.Value)
                return false;

            return !IsStaleWhileRevalidate(now);
        }

        public CacheEntry Clone()
        {
            return new CacheEntry(
                key: Key,
                statusCode: StatusCode,
                headers: Headers,
                body: Body,
                cachedAtUtc: CachedAtUtc,
                expiresAtUtc: ExpiresAtUtc,
                eTag: ETag,
                lastModified: LastModified,
                responseUrl: ResponseUrl,
                varyHeaders: VaryHeaders,
                varyKey: VaryKey,
                staleWhileRevalidate: StaleWhileRevalidate,
                mustRevalidate: MustRevalidate,
                trailers: Trailers);
        }

        private static DateTime EnsureUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);

            return value.ToUniversalTime();
        }

        private static IReadOnlyList<string> NormalizeVaryHeaders(IReadOnlyList<string> varyHeaders)
        {
            if (varyHeaders == null || varyHeaders.Count == 0)
                return Array.Empty<string>();

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < varyHeaders.Count; i++)
            {
                var raw = varyHeaders[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                    continue;

                set.Add(trimmed);
            }

            if (set.Count == 0)
                return Array.Empty<string>();

            var normalized = new string[set.Count];
            set.CopyTo(normalized);
            Array.Sort(normalized, StringComparer.OrdinalIgnoreCase);
            return normalized;
        }
    }
}
