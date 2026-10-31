using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    public enum CookieSameSite
    {
        Unspecified,
        Lax,
        Strict,
        None
    }

    /// <summary>
    /// Thread-safe RFC 6265 style cookie jar with bounded in-memory storage.
    /// </summary>
    public sealed class CookieJar : IDisposable
    {
        private static readonly HashSet<string> CommonSecondLevelPublicSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ac",
            "co",
            "com",
            "edu",
            "gov",
            "net",
            "org",
            "ne",
            "or",
            "go",
            "mil"
        };

        // Heuristic-only denylist for common multi-label public suffixes.
        // This is not a complete PSL implementation; add/adjust entries as needed.
        private static readonly HashSet<string> KnownMultiLabelPublicSuffixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ac.jp",
            "ac.in",
            "ac.nz",
            "ac.uk",
            "co.in",
            "co.jp",
            "co.nz",
            "co.uk",
            "com.au",
            "com.br",
            "com.cn",
            "com.mx",
            "com.sg",
            "com.tr",
            "edu.au",
            "edu.cn",
            "edu.sg",
            "edu.tr",
            "firm.in",
            "gen.in",
            "gob.mx",
            "go.jp",
            "gov.cn",
            "gov.sg",
            "gov.tr",
            "gov.au",
            "gov.uk",
            "ind.in",
            "ne.jp",
            "net.br",
            "net.cn",
            "net.in",
            "net.au",
            "net.nz",
            "net.sg",
            "net.tr",
            "nic.in",
            "org.br",
            "org.cn",
            "org.in",
            "org.mx",
            "org.au",
            "org.nz",
            "org.uk",
            "org.sg",
            "org.tr",
            "res.in",
            "sch.uk",
            // Shared-hosting suffixes from the private section of the Public Suffix List: every
            // tenant gets a subdomain, so a Domain cookie on the suffix would reach all tenants.
            "amazonaws.com",
            "appspot.com",
            "azurewebsites.net",
            "azurestaticapps.net",
            "blogspot.com",
            "cloudapp.net",
            "cloudfront.net",
            "elasticbeanstalk.com",
            "firebaseapp.com",
            "fly.dev",
            "github.io",
            "githubusercontent.com",
            "gitlab.io",
            "glitch.me",
            "herokuapp.com",
            "netlify.app",
            "ngrok.io",
            "onrender.com",
            "pages.dev",
            "vercel.app",
            "web.app",
            "workers.dev"
        };

        /// <summary>RFC 6265bis 5.6: a cookie whose name plus value exceeds this is ignored.</summary>
        internal const int MaxNameValueLength = 4096;

        /// <summary>RFC 6265bis 5.6: an attribute value longer than this is ignored.</summary>
        internal const int MaxAttributeValueLength = 1024;

        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly Dictionary<string, CookieRecord> _cookies = new Dictionary<string, CookieRecord>(StringComparer.Ordinal);
        private readonly int _maxCookiesPerDomain;
        private readonly int _maxTotalCookies;
        private readonly Func<string, bool> _isPublicSuffix;
        private int _disposed;

        private sealed class CookieRecord
        {
            public string Name;
            public string Value;
            public string Domain;
            public bool HostOnly;
            public string Path;
            public DateTime? ExpiresAtUtc;
            public bool Secure;
            public bool HttpOnly;
            public CookieSameSite SameSite;
            public DateTime CreatedAtUtc;

            // Written by concurrent readers of GetCookieHeader under the read lock: ticks updated
            // with Interlocked so 32-bit IL2CPP never tears the 64-bit value.
            public long LastAccessedTicks;
        }

        /// <summary>Creates an empty, bounded cookie jar.</summary>
        /// <param name="maxCookiesPerDomain">Per-domain cookie limit; least recently used cookies are evicted first.</param>
        /// <param name="maxTotalCookies">Total cookie limit; least recently used cookies are evicted first.</param>
        /// <param name="isPublicSuffix">
        /// Optional predicate that reports whether a lower-case domain (no leading dot) is a public
        /// suffix, for example backed by the full Public Suffix List. It is consulted in addition to
        /// the built-in list: a <c>Domain</c> attribute naming a public suffix is rejected unless it
        /// equals the request host, in which case the cookie becomes host-only (RFC 6265 section 5.3
        /// step 5). Called under the jar's write lock; it must be fast, thread-safe and must not call
        /// back into the jar.
        /// </param>
        public CookieJar(int maxCookiesPerDomain = 50, int maxTotalCookies = 3000, Func<string, bool> isPublicSuffix = null)
        {
            if (maxCookiesPerDomain <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCookiesPerDomain), maxCookiesPerDomain, "Must be > 0.");
            if (maxTotalCookies <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxTotalCookies), maxTotalCookies, "Must be > 0.");

            _maxCookiesPerDomain = maxCookiesPerDomain;
            _maxTotalCookies = maxTotalCookies;
            _isPublicSuffix = isPublicSuffix;
        }

        public int Count
        {
            get
            {
                ThrowIfDisposed();
                _lock.EnterReadLock();
                try
                {
                    return _cookies.Count;
                }
                finally
                {
                    _lock.ExitReadLock();
                }
            }
        }

        public int GetDomainCount(string domain)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(domain))
                return 0;

            var normalizedDomain = NormalizeDomain(domain);
            _lock.EnterReadLock();
            try
            {
                int count = 0;
                foreach (var cookie in _cookies.Values)
                {
                    if (string.Equals(cookie.Domain, normalizedDomain, StringComparison.Ordinal))
                        count++;
                }

                return count;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public void StoreFromSetCookieHeaders(
            Uri requestUri,
            IReadOnlyList<string> setCookieHeaders,
            DateTime? utcNowOverride = null)
        {
            ThrowIfDisposed();

            if (requestUri == null)
                throw new ArgumentNullException(nameof(requestUri));
            if (setCookieHeaders == null || setCookieHeaders.Count == 0)
                return;

            var nowUtc = EnsureUtcNow(utcNowOverride);
            bool isSecureOrigin = string.Equals(requestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

            _lock.EnterWriteLock();
            try
            {
                RemoveExpiredUnsafe(nowUtc);

                for (int i = 0; i < setCookieHeaders.Count; i++)
                {
                    var line = setCookieHeaders[i];
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (!TryParseSetCookie(requestUri, line, nowUtc, _isPublicSuffix, out var cookie, out var shouldDelete))
                        continue;

                    // RFC 6265bis 5.7 ("leave secure cookies alone"): a non-secure origin can neither
                    // create, overwrite nor delete a cookie that shadows an existing Secure cookie.
                    if (!cookie.Secure && !isSecureOrigin && ShadowsSecureCookieUnsafe(cookie))
                        continue;

                    var key = BuildCookieKey(cookie.Name, cookie.Domain, cookie.Path);
                    if (shouldDelete)
                    {
                        _cookies.Remove(key);
                        continue;
                    }

                    _cookies[key] = cookie;
                }

                EnforceLimitsUnsafe();
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        public string GetCookieHeader(
            Uri requestUri,
            HttpMethod method,
            bool isCrossSiteRequest,
            DateTime? utcNowOverride = null)
        {
            ThrowIfDisposed();

            if (requestUri == null)
                throw new ArgumentNullException(nameof(requestUri));

            var nowUtc = EnsureUtcNow(utcNowOverride);
            var nowTicks = nowUtc.Ticks;
            bool sawExpired = false;
            string header = null;

            // Readers share the lock; the only writes are the per-cookie last-access stamp
            // (Interlocked) and the expired-cookie sweep, which runs after the read section under
            // the write lock.
            _lock.EnterReadLock();
            try
            {
                var host = NormalizeDomain(requestUri.Host);
                var path = string.IsNullOrEmpty(requestUri.AbsolutePath) ? "/" : requestUri.AbsolutePath;
                bool isHttps = string.Equals(requestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase);

                var matches = new List<CookieRecord>();
                foreach (var cookie in _cookies.Values)
                {
                    if (cookie.ExpiresAtUtc.HasValue && cookie.ExpiresAtUtc.Value <= nowUtc)
                    {
                        sawExpired = true;
                        continue;
                    }

                    if (cookie.Secure && !isHttps)
                        continue;

                    if (!DomainMatches(host, cookie.Domain, cookie.HostOnly))
                        continue;

                    if (!PathMatches(path, cookie.Path))
                        continue;

                    if (!SameSiteAllows(cookie.SameSite, method, isCrossSiteRequest))
                        continue;

                    Interlocked.Exchange(ref cookie.LastAccessedTicks, nowTicks);
                    matches.Add(cookie);
                }

                if (matches.Count > 0)
                {
                    matches.Sort((a, b) =>
                    {
                        int byPath = b.Path.Length.CompareTo(a.Path.Length);
                        if (byPath != 0)
                            return byPath;

                        int byCreated = a.CreatedAtUtc.CompareTo(b.CreatedAtUtc);
                        if (byCreated != 0)
                            return byCreated;

                        return string.CompareOrdinal(a.Name, b.Name);
                    });

                    header = BuildCookieHeader(matches);
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }

            if (sawExpired)
            {
                _lock.EnterWriteLock();
                try
                {
                    RemoveExpiredUnsafe(nowUtc);
                }
                finally
                {
                    _lock.ExitWriteLock();
                }
            }

            return header;
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _lock.Dispose();
        }

        private static bool TryParseSetCookie(
            Uri requestUri,
            string line,
            DateTime nowUtc,
            Func<string, bool> isPublicSuffix,
            out CookieRecord cookie,
            out bool shouldDelete)
        {
            cookie = null;
            shouldDelete = false;

            var segments = line.Split(';');
            if (segments.Length == 0)
                return false;

            var nameValue = segments[0].Trim();
            int eqIndex = nameValue.IndexOf('=');
            if (eqIndex <= 0)
                return false;

            var name = nameValue.Substring(0, eqIndex).Trim();
            var value = UnquoteCookieValue(nameValue.Substring(eqIndex + 1).Trim());
            if (name.Length == 0)
                return false;

            if (name.Length + value.Length > MaxNameValueLength)
                return false;

            var host = NormalizeDomain(requestUri.Host);
            bool isIpHost = requestUri.HostNameType == UriHostNameType.IPv4 ||
                            requestUri.HostNameType == UriHostNameType.IPv6;
            string domain = host;
            bool hostOnly = true;
            bool hasDomainAttribute = false;
            bool hasPathAttribute = false;
            string path = DefaultPath(requestUri.AbsolutePath);
            DateTime? expiresUtc = null;
            DateTime? maxAgeExpiresUtc = null;
            bool hasMaxAge = false;
            bool secure = false;
            bool httpOnly = false;
            CookieSameSite sameSite = CookieSameSite.Unspecified;

            for (int i = 1; i < segments.Length; i++)
            {
                var segment = segments[i].Trim();
                if (segment.Length == 0)
                    continue;

                int attrEq = segment.IndexOf('=');
                string attrName = attrEq > 0
                    ? segment.Substring(0, attrEq).Trim()
                    : segment;
                string attrValue = attrEq > 0
                    ? segment.Substring(attrEq + 1).Trim()
                    : string.Empty;

                // RFC 6265bis 5.6: an oversized attribute value is ignored, not the cookie.
                if (attrValue.Length > MaxAttributeValueLength)
                    continue;

                if (string.Equals(attrName, "Domain", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(attrValue))
                        return false;

                    var normalizedDomain = NormalizeDomain(attrValue.TrimStart('.'));

                    if (isIpHost)
                    {
                        // RFC 6265 5.1.3: an IP address only domain-matches the identical string,
                        // so the cookie stays bound to exactly this host.
                        if (!string.Equals(normalizedDomain.Trim('[', ']'), host.Trim('[', ']'), StringComparison.Ordinal))
                            return false;

                        domain = host;
                        hostOnly = true;
                        hasDomainAttribute = true;
                        continue;
                    }

                    if (IsPublicSuffix(normalizedDomain, isPublicSuffix))
                    {
                        // RFC 6265 5.3 step 5: a public-suffix Domain equal to the request host
                        // degrades to a host-only cookie; any other public suffix is rejected.
                        if (!string.Equals(normalizedDomain, host, StringComparison.Ordinal))
                            return false;

                        domain = host;
                        hostOnly = true;
                        hasDomainAttribute = false;
                        continue;
                    }

                    if (!DomainMatches(host, normalizedDomain, hostOnly: false))
                        return false;

                    domain = normalizedDomain;
                    hostOnly = false;
                    hasDomainAttribute = true;
                    continue;
                }

                if (string.Equals(attrName, "Path", StringComparison.OrdinalIgnoreCase))
                {
                    hasPathAttribute = true;
                    path = !string.IsNullOrEmpty(attrValue) && attrValue[0] == '/'
                        ? attrValue
                        : DefaultPath(requestUri.AbsolutePath);
                    continue;
                }

                if (string.Equals(attrName, "Expires", StringComparison.OrdinalIgnoreCase))
                {
                    if (DateTime.TryParse(
                        attrValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out var parsedExpires))
                    {
                        expiresUtc = parsedExpires;
                    }

                    continue;
                }

                if (string.Equals(attrName, "Max-Age", StringComparison.OrdinalIgnoreCase))
                {
                    if (long.TryParse(attrValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
                    {
                        hasMaxAge = true;
                        if (seconds <= 0)
                        {
                            maxAgeExpiresUtc = nowUtc;
                            shouldDelete = true;
                        }
                        else
                        {
                            maxAgeExpiresUtc = AddSecondsClamped(nowUtc, seconds);
                        }
                    }

                    continue;
                }

                if (string.Equals(attrName, "Secure", StringComparison.OrdinalIgnoreCase))
                {
                    secure = true;
                    continue;
                }

                if (string.Equals(attrName, "HttpOnly", StringComparison.OrdinalIgnoreCase))
                {
                    httpOnly = true;
                    continue;
                }

                if (string.Equals(attrName, "SameSite", StringComparison.OrdinalIgnoreCase))
                {
                    sameSite = ParseSameSite(attrValue);
                }
            }

            if (hasMaxAge)
                expiresUtc = maxAgeExpiresUtc;

            if (expiresUtc.HasValue && expiresUtc.Value <= nowUtc)
                shouldDelete = true;

            if (secure && !string.Equals(requestUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return false;

            // RFC 6265bis 4.1.3 / 5.7: cookie name prefixes (case-insensitive).
            if (name.StartsWith("__Secure-", StringComparison.OrdinalIgnoreCase) && !secure)
                return false;

            if (name.StartsWith("__Host-", StringComparison.OrdinalIgnoreCase) &&
                (!secure || hasDomainAttribute || !hostOnly || !hasPathAttribute || !string.Equals(path, "/", StringComparison.Ordinal)))
            {
                return false;
            }

            cookie = new CookieRecord
            {
                Name = name,
                Value = value,
                Domain = domain,
                HostOnly = hostOnly,
                Path = NormalizePath(path),
                ExpiresAtUtc = expiresUtc,
                Secure = secure,
                HttpOnly = httpOnly,
                SameSite = sameSite,
                CreatedAtUtc = nowUtc,
                LastAccessedTicks = nowUtc.Ticks
            };

            return true;
        }

        private void EnforceLimitsUnsafe()
        {
            RemoveExpiredUnsafe(DateTime.UtcNow);

            var domainCounts = BuildDomainCountsUnsafe();

            while (_cookies.Count > _maxTotalCookies)
            {
                if (!TryEvictOldestUnsafe(domain: null, out var evictedDomain))
                    break;

                if (!string.IsNullOrEmpty(evictedDomain)
                    && domainCounts.TryGetValue(evictedDomain, out var count))
                {
                    domainCounts[evictedDomain] = Math.Max(0, count - 1);
                }
            }

            var domains = new List<string>(domainCounts.Keys);
            for (int i = 0; i < domains.Count; i++)
            {
                var domain = domains[i];
                if (!domainCounts.TryGetValue(domain, out var count))
                    continue;

                while (count > _maxCookiesPerDomain)
                {
                    if (!TryEvictOldestUnsafe(domain, out _))
                        break;

                    count--;
                }

                domainCounts[domain] = count;
            }
        }

        private Dictionary<string, int> BuildDomainCountsUnsafe()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cookie in _cookies.Values)
            {
                if (!counts.TryGetValue(cookie.Domain, out var count))
                    count = 0;

                counts[cookie.Domain] = count + 1;
            }

            return counts;
        }

        private bool TryEvictOldestUnsafe(string domain, out string evictedDomain)
        {
            CookieRecord oldest = null;
            string oldestKey = null;

            foreach (var pair in _cookies)
            {
                var candidate = pair.Value;
                if (domain != null && !string.Equals(candidate.Domain, domain, StringComparison.Ordinal))
                    continue;

                if (oldest == null
                    || candidate.LastAccessedTicks < oldest.LastAccessedTicks
                    || (candidate.LastAccessedTicks == oldest.LastAccessedTicks && candidate.CreatedAtUtc < oldest.CreatedAtUtc)
                    || (candidate.LastAccessedTicks == oldest.LastAccessedTicks
                        && candidate.CreatedAtUtc == oldest.CreatedAtUtc
                        && string.CompareOrdinal(pair.Key, oldestKey) < 0))
                {
                    oldest = candidate;
                    oldestKey = pair.Key;
                }
            }

            if (oldestKey == null)
            {
                evictedDomain = null;
                return false;
            }

            evictedDomain = oldest.Domain;
            _cookies.Remove(oldestKey);
            return true;
        }

        private void RemoveExpiredUnsafe(DateTime nowUtc)
        {
            var expiredKeys = new List<string>();
            foreach (var pair in _cookies)
            {
                if (pair.Value.ExpiresAtUtc.HasValue && pair.Value.ExpiresAtUtc.Value <= nowUtc)
                    expiredKeys.Add(pair.Key);
            }

            for (int i = 0; i < expiredKeys.Count; i++)
                _cookies.Remove(expiredKeys[i]);
        }

        private static bool DomainMatches(string host, string cookieDomain, bool hostOnly)
        {
            if (hostOnly)
                return string.Equals(host, cookieDomain, StringComparison.Ordinal);

            if (string.Equals(host, cookieDomain, StringComparison.Ordinal))
                return true;

            if (host.Length <= cookieDomain.Length)
                return false;

            int suffixStart = host.Length - cookieDomain.Length;
            return host[suffixStart - 1] == '.'
                   && host.EndsWith(cookieDomain, StringComparison.Ordinal);
        }

        private static bool PathMatches(string requestPath, string cookiePath)
        {
            if (requestPath == null)
                requestPath = "/";
            if (cookiePath == null)
                cookiePath = "/";

            if (string.Equals(requestPath, cookiePath, StringComparison.Ordinal))
                return true;

            if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
                return false;

            if (cookiePath.EndsWith("/", StringComparison.Ordinal))
                return true;

            return requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/';
        }

        private static bool SameSiteAllows(CookieSameSite sameSite, HttpMethod method, bool isCrossSiteRequest)
        {
            if (!isCrossSiteRequest)
                return true;

            if (sameSite == CookieSameSite.Strict)
                return false;

            if (sameSite == CookieSameSite.Lax)
                return method == HttpMethod.GET || method == HttpMethod.HEAD || method == HttpMethod.OPTIONS;

            return true;
        }

        private static string BuildCookieKey(string name, string domain, string path)
        {
            return name + "|" + domain + "|" + path;
        }

        private static string NormalizeDomain(string domain)
        {
            return domain?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "/";

            if (path[0] != '/')
                return "/";

            return path;
        }

        private static string BuildCookieHeader(List<CookieRecord> matches)
        {
            if (matches == null || matches.Count == 0)
                return null;

            var sb = new StringBuilder(matches.Count * 24);
            for (int i = 0; i < matches.Count; i++)
            {
                if (i > 0)
                    sb.Append("; ");

                sb.Append(matches[i].Name);
                sb.Append('=');
                sb.Append(matches[i].Value);
            }

            return sb.ToString();
        }

        private static string DefaultPath(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath) || absolutePath[0] != '/')
                return "/";

            int lastSlash = absolutePath.LastIndexOf('/');
            if (lastSlash <= 0)
                return "/";

            return absolutePath.Substring(0, lastSlash);
        }

        private static CookieSameSite ParseSameSite(string value)
        {
            if (string.Equals(value, "Lax", StringComparison.OrdinalIgnoreCase))
                return CookieSameSite.Lax;
            if (string.Equals(value, "Strict", StringComparison.OrdinalIgnoreCase))
                return CookieSameSite.Strict;
            if (string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
                return CookieSameSite.None;
            return CookieSameSite.Unspecified;
        }

        private static DateTime EnsureUtcNow(DateTime? utcNowOverride)
        {
            if (!utcNowOverride.HasValue)
                return DateTime.UtcNow;

            var value = utcNowOverride.Value;
            if (value.Kind == DateTimeKind.Utc)
                return value;

            if (value.Kind == DateTimeKind.Unspecified)
                return DateTime.SpecifyKind(value, DateTimeKind.Utc);

            return value.ToUniversalTime();
        }

        private static string UnquoteCookieValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                return value.Substring(1, value.Length - 2);

            return value;
        }

        private bool ShadowsSecureCookieUnsafe(CookieRecord candidate)
        {
            foreach (var existing in _cookies.Values)
            {
                if (!existing.Secure || !string.Equals(existing.Name, candidate.Name, StringComparison.Ordinal))
                    continue;

                bool domainsOverlap = DomainMatches(existing.Domain, candidate.Domain, hostOnly: false)
                                      || DomainMatches(candidate.Domain, existing.Domain, hostOnly: false);
                if (domainsOverlap && PathMatches(candidate.Path, existing.Path))
                    return true;
            }

            return false;
        }

        private static bool IsPublicSuffix(string normalizedDomain, Func<string, bool> isPublicSuffix)
        {
            if (IsRejectedPublicSuffix(normalizedDomain))
                return true;

            return isPublicSuffix != null && isPublicSuffix(normalizedDomain);
        }

        private static bool IsRejectedPublicSuffix(string normalizedDomain)
        {
            if (string.IsNullOrEmpty(normalizedDomain))
                return true;

            var labels = normalizedDomain.Split('.');
            if (labels.Length < 2)
                return true;

            if (KnownMultiLabelPublicSuffixes.Contains(normalizedDomain))
                return true;

            if (labels.Length == 2
                && labels[1].Length == 2
                && CommonSecondLevelPublicSuffixes.Contains(labels[0]))
            {
                return true;
            }

            return false;
        }

        private static DateTime AddSecondsClamped(DateTime utcNow, long seconds)
        {
            try
            {
                return utcNow.AddSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(CookieJar));
        }
    }
}
