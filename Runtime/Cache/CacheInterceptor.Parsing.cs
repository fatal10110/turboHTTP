using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    public sealed partial class CacheInterceptor
    {
        private static CacheControlDirectives ParseCacheControl(IReadOnlyList<string> values)
        {
            bool noStore = false;
            bool noCache = false;
            bool @private = false;
            bool @public = false;
            bool mustRevalidate = false;
            TimeSpan? sharedMaxAge = null;
            TimeSpan? maxAge = null;
            TimeSpan? staleWhileRevalidate = null;

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

                        if (directive.StartsWith("no-cache", StringComparison.OrdinalIgnoreCase))
                        {
                            var remainder = directive.Substring("no-cache".Length).TrimStart();
                            if (remainder.Length > 0 && remainder[0] != '=')
                                continue;

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

                        if (directive.StartsWith("stale-while-revalidate=", StringComparison.OrdinalIgnoreCase))
                        {
                            var secondsPart = directive.Substring("stale-while-revalidate=".Length).Trim().Trim('"');
                            if (!long.TryParse(secondsPart, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                                continue;

                            if (seconds < 0)
                                continue;

                            staleWhileRevalidate = TimeSpan.FromSeconds(seconds);
                        }
                    }
                }
            }

            return new CacheControlDirectives(
                noStore,
                noCache,
                @private,
                @public,
                mustRevalidate,
                sharedMaxAge,
                maxAge,
                staleWhileRevalidate);
        }

        private static DateTime? ResolveExpiry(
            DateTime nowUtc,
            CacheControlDirectives cacheControl,
            HttpHeaders headers,
            bool isSharedCache)
        {
            // RFC 9111 Section 5.2.2.10: s-maxage applies only to shared caches.
            var freshnessLifetime = (isSharedCache && cacheControl.SharedMaxAge.HasValue)
                ? cacheControl.SharedMaxAge
                : cacheControl.MaxAge;

            if (!freshnessLifetime.HasValue)
            {
                var expiresValue = headers.Get("Expires");
                if (string.IsNullOrWhiteSpace(expiresValue))
                    return null;

                expiresValue = expiresValue.Trim();
                if (expiresValue == "0" || expiresValue == "-1")
                    return nowUtc.AddSeconds(-1);

                if (!TryParseHttpDate(expiresValue, out var expiresUtc))
                    return null;

                // RFC 9111 Section 4.2.1: freshness_lifetime = Expires - Date, both from the origin's clock, so the
                // device clock's offset from the origin does not move the expiry. Callers ensure Date is present
                // (the receive time when the origin sent none, RFC 9110 Section 6.6.1).
                var dateUtc = TryParseHttpDate(headers.Get("Date"), out var parsedDate) ? parsedDate : nowUtc;
                freshnessLifetime = expiresUtc - dateUtc;
            }

            // RFC 9111 Section 4.2.3: corrected_initial_age is the corrected_age_value (the Age field; the
            // conservative max with apparent_age is optional and would reintroduce the device clock skew).
            // The entry is stale once corrected_initial_age + resident_time reaches the lifetime.
            var upstreamAgeSeconds = ParseAgeSeconds(headers);
            var remainingLifetime = freshnessLifetime.Value - TimeSpan.FromSeconds(upstreamAgeSeconds);
            return AddClamped(nowUtc, remainingLifetime);
        }

        // RFC 9110 Section 6.6.1: a recipient with a clock that caches a response without a (valid) Date appends one
        // carrying the receive time.
        private static void EnsureDateHeader(HttpHeaders headers, DateTime receivedAtUtc)
        {
            if (TryParseHttpDate(headers.Get("Date"), out _))
                return;

            headers.Set("Date", receivedAtUtc.ToString("r", CultureInfo.InvariantCulture));
        }

        private static bool TryParseHttpDate(string value, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!DateTime.TryParse(
                value.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
            {
                return false;
            }

            utc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            return true;
        }

        // Peer-controlled lifetimes (Expires far from Date, huge max-age) must not overflow DateTime.
        private static DateTime AddClamped(DateTime utc, TimeSpan offset)
        {
            if (offset > TimeSpan.Zero && offset.Ticks > DateTime.MaxValue.Ticks - utc.Ticks)
                return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

            if (offset < TimeSpan.Zero && -offset.Ticks > utc.Ticks - DateTime.MinValue.Ticks)
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

            return utc.Add(offset);
        }

        private static bool IsNotModifiedMergeExcluded(string name)
        {
            for (int i = 0; i < NotModifiedMergeExcludedHeaders.Length; i++)
            {
                if (string.Equals(name, NotModifiedMergeExcludedHeaders[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
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

        private static List<Uri> CollectInvalidationTargets(Uri requestUri, HttpHeaders responseHeaders)
        {
            var results = new List<Uri>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            AddInvalidationTarget(results, seen, requestUri);
            AddHeaderInvalidationTargets(results, seen, requestUri, responseHeaders.GetValues("Location"));
            AddHeaderInvalidationTargets(results, seen, requestUri, responseHeaders.GetValues("Content-Location"));

            return results;
        }

        private static void AddHeaderInvalidationTargets(
            List<Uri> results,
            HashSet<string> seen,
            Uri requestUri,
            IReadOnlyList<string> values)
        {
            for (int i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!Uri.TryCreate(requestUri, value.Trim(), out var resolved))
                    continue;

                if (!IsSameAuthority(requestUri, resolved))
                    continue;

                AddInvalidationTarget(results, seen, resolved);
            }
        }

        private static void AddInvalidationTarget(List<Uri> results, HashSet<string> seen, Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri)
                return;

            var key = uri.AbsoluteUri;
            if (!seen.Add(key))
                return;

            results.Add(uri);
        }

        private static bool IsSameAuthority(Uri left, Uri right)
        {
            if (left == null || right == null || !left.IsAbsoluteUri || !right.IsAbsoluteUri)
                return false;

            return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
                   && left.Port == right.Port;
        }

        private static void StripHopByHopHeaders(HttpHeaders headers)
        {
            var connectionTokens = new List<string>();
            var connectionValues = headers.GetValues("Connection");
            for (int i = 0; i < connectionValues.Count; i++)
            {
                var line = connectionValues[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                var tokens = line.Split(',');
                for (int j = 0; j < tokens.Length; j++)
                {
                    var token = tokens[j].Trim();
                    if (token.Length > 0)
                        connectionTokens.Add(token);
                }
            }

            for (int i = 0; i < HopByHopHeaderNames.Length; i++)
                headers.Remove(HopByHopHeaderNames[i]);

            for (int i = 0; i < connectionTokens.Count; i++)
                headers.Remove(connectionTokens[i]);
        }

        private static int ParseAgeSeconds(HttpHeaders headers)
        {
            if (headers == null)
                return 0;

            var values = headers.GetValues("Age");
            for (int i = 0; i < values.Count; i++)
            {
                var raw = values[i];
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                if (!long.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                    continue;

                if (parsed <= 0)
                    return 0;

                if (parsed >= int.MaxValue)
                    return int.MaxValue;

                return (int)parsed;
            }

            return 0;
        }

        private static int ComputeCurrentAgeSeconds(CacheEntry entry, DateTime nowUtc)
        {
            var correctedInitialAge = ParseAgeSeconds(entry.Headers);
            var residentTimeSeconds = nowUtc <= entry.CachedAtUtc
                ? 0L
                : (long)Math.Floor((nowUtc - entry.CachedAtUtc).TotalSeconds);

            var total = (long)correctedInitialAge + residentTimeSeconds;
            if (total <= 0)
                return 0;

            if (total >= int.MaxValue)
                return int.MaxValue;

            return (int)total;
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
    }
}
