using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Immutable redaction policy shared by <see cref="LoggingInterceptor"/>, <see cref="MonitorInterceptor"/>
    /// and the Editor HTTP Monitor. Redaction is on by default: values of sensitive headers, URL query
    /// values (names are kept) and URL user-info are replaced by <see cref="RedactedValue"/>.
    /// </summary>
    /// <remarks>
    /// Instances are immutable and safe to share across threads. Derive a custom policy with
    /// <see cref="WithSensitiveHeaders"/>, <see cref="WithoutSensitiveHeaders"/> and
    /// <see cref="WithQueryValueRedaction"/>; opt out entirely with <see cref="None"/>.
    /// </remarks>
    public sealed class RedactionPolicy
    {
        /// <summary>Placeholder that replaces a redacted value.</summary>
        public const string RedactedValue = "[REDACTED]";

        private static readonly string[] DefaultHeaderNames =
        {
            "Authorization",
            "Proxy-Authorization",
            "WWW-Authenticate",
            "Proxy-Authenticate",
            "Cookie",
            "Set-Cookie",
            "X-Api-Key",
            "Api-Key",
            "X-Auth-Token",
            "X-Amz-Security-Token",
            "X-Goog-Api-Key",
            "X-CSRF-Token",
            "X-XSRF-Token"
        };

        private readonly HashSet<string> _sensitiveHeaders;

        /// <summary>
        /// Default header names treated as sensitive (case-insensitive).
        /// </summary>
        public static IReadOnlyList<string> DefaultSensitiveHeaderNames { get; } =
            new ReadOnlyCollection<string>(DefaultHeaderNames);

        /// <summary>
        /// Default policy: <see cref="DefaultSensitiveHeaderNames"/> and URL query values are redacted.
        /// </summary>
        public static RedactionPolicy Default { get; } = new RedactionPolicy();

        /// <summary>
        /// Policy that redacts nothing. Use only for local debugging of non-sensitive traffic.
        /// </summary>
        public static RedactionPolicy None { get; } =
            new RedactionPolicy(Array.Empty<string>(), redactQueryValues: false);

        /// <summary>
        /// Creates a policy.
        /// </summary>
        /// <param name="sensitiveHeaderNames">
        /// Header names whose values are redacted (case-insensitive). <c>null</c> uses
        /// <see cref="DefaultSensitiveHeaderNames"/>.
        /// </param>
        /// <param name="redactQueryValues">
        /// When <c>true</c> (default), every URL query value and URL user-info is replaced by
        /// <see cref="RedactedValue"/>; query parameter names are kept.
        /// </param>
        public RedactionPolicy(IEnumerable<string> sensitiveHeaderNames = null, bool redactQueryValues = true)
        {
            _sensitiveHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in sensitiveHeaderNames ?? DefaultHeaderNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    _sensitiveHeaders.Add(name.Trim());
            }

            RedactQueryValues = redactQueryValues;
        }

        /// <summary>Header names whose values are redacted.</summary>
        public IReadOnlyCollection<string> SensitiveHeaderNames => _sensitiveHeaders;

        /// <summary>Whether URL query values and URL user-info are redacted.</summary>
        public bool RedactQueryValues { get; }

        /// <summary>Whether this policy redacts anything at all.</summary>
        public bool IsEnabled => RedactQueryValues || _sensitiveHeaders.Count > 0;

        /// <summary>Returns a copy of this policy that also redacts <paramref name="headerNames"/>.</summary>
        public RedactionPolicy WithSensitiveHeaders(params string[] headerNames)
        {
            var names = new List<string>(_sensitiveHeaders);
            if (headerNames != null)
                names.AddRange(headerNames);
            return new RedactionPolicy(names, RedactQueryValues);
        }

        /// <summary>Returns a copy of this policy that no longer redacts <paramref name="headerNames"/>.</summary>
        public RedactionPolicy WithoutSensitiveHeaders(params string[] headerNames)
        {
            var names = new HashSet<string>(_sensitiveHeaders, StringComparer.OrdinalIgnoreCase);
            if (headerNames != null)
            {
                for (int i = 0; i < headerNames.Length; i++)
                {
                    if (headerNames[i] != null)
                        names.Remove(headerNames[i].Trim());
                }
            }

            return new RedactionPolicy(names, RedactQueryValues);
        }

        /// <summary>Returns a copy of this policy with URL query-value redaction set to <paramref name="enabled"/>.</summary>
        public RedactionPolicy WithQueryValueRedaction(bool enabled)
        {
            return enabled == RedactQueryValues
                ? this
                : new RedactionPolicy(_sensitiveHeaders, enabled);
        }

        /// <summary>Whether the value of <paramref name="headerName"/> is redacted.</summary>
        public bool IsSensitiveHeader(string headerName)
        {
            return !string.IsNullOrEmpty(headerName) && _sensitiveHeaders.Contains(headerName.Trim());
        }

        /// <summary>
        /// Returns <see cref="RedactedValue"/> when <paramref name="headerName"/> is sensitive, else
        /// <paramref name="value"/>.
        /// </summary>
        public string RedactHeaderValue(string headerName, string value)
        {
            return IsSensitiveHeader(headerName) ? RedactedValue : value;
        }

        /// <summary>
        /// Returns the URL for display or storage with query values and user-info redacted (when
        /// <see cref="RedactQueryValues"/> is set). Returns <c>null</c> for a <c>null</c> URI.
        /// </summary>
        public string RedactUrl(Uri uri)
        {
            if (uri == null)
                return null;

            if (!RedactQueryValues)
                return uri.ToString();

            return RedactUrl(uri.IsAbsoluteUri ? uri.AbsoluteUri : uri.OriginalString);
        }

        /// <summary>
        /// Returns <paramref name="url"/> with query values and user-info redacted (when
        /// <see cref="RedactQueryValues"/> is set). Query parameter names and the fragment are kept.
        /// </summary>
        public string RedactUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || !RedactQueryValues)
                return url;

            var fragmentIndex = url.IndexOf('#');
            var beforeFragment = fragmentIndex >= 0 ? url.Substring(0, fragmentIndex) : url;
            var fragment = fragmentIndex >= 0 ? url.Substring(fragmentIndex) : string.Empty;

            var queryIndex = beforeFragment.IndexOf('?');
            var beforeQuery = queryIndex >= 0 ? beforeFragment.Substring(0, queryIndex) : beforeFragment;
            var query = queryIndex >= 0 ? beforeFragment.Substring(queryIndex + 1) : null;

            var builder = new StringBuilder(url.Length + 16);
            AppendWithoutUserInfo(builder, beforeQuery);
            if (query != null)
            {
                builder.Append('?');
                AppendRedactedQuery(builder, query);
            }

            builder.Append(fragment);
            return builder.ToString();
        }

        private static void AppendWithoutUserInfo(StringBuilder builder, string beforeQuery)
        {
            var schemeEnd = beforeQuery.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 0)
            {
                builder.Append(beforeQuery);
                return;
            }

            var authorityStart = schemeEnd + 3;
            var authorityEnd = beforeQuery.IndexOf('/', authorityStart);
            if (authorityEnd < 0)
                authorityEnd = beforeQuery.Length;

            var at = beforeQuery.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart);
            if (at < 0)
            {
                builder.Append(beforeQuery);
                return;
            }

            builder.Append(beforeQuery, 0, authorityStart)
                .Append(RedactedValue)
                .Append(beforeQuery, at, beforeQuery.Length - at);
        }

        private static void AppendRedactedQuery(StringBuilder builder, string query)
        {
            var start = 0;
            while (start <= query.Length)
            {
                var end = query.IndexOf('&', start);
                if (end < 0)
                    end = query.Length;

                var pair = query.Substring(start, end - start);
                var equals = pair.IndexOf('=');
                if (equals < 0)
                {
                    builder.Append(pair);
                }
                else
                {
                    builder.Append(pair, 0, equals + 1);
                    if (equals + 1 < pair.Length)
                        builder.Append(RedactedValue);
                }

                if (end >= query.Length)
                    break;

                builder.Append('&');
                start = end + 1;
            }
        }
    }
}
