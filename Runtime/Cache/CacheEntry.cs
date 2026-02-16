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
        public string Key { get; }
        public HttpStatusCode StatusCode { get; }
        public HttpHeaders Headers { get; }
        public ReadOnlyMemory<byte> Body { get; }
        public DateTime CachedAtUtc { get; }
        public DateTime? ExpiresAtUtc { get; }
        public string ETag { get; }
        public string LastModified { get; }
        public Uri ResponseUrl { get; }
        public IReadOnlyList<string> VaryHeaders { get; }
        public string VaryKey { get; }
        public bool MustRevalidate { get; }

        public int BodyLength => Body.Length;

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
            bool mustRevalidate)
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
            MustRevalidate = mustRevalidate;
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
                mustRevalidate: MustRevalidate);
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
