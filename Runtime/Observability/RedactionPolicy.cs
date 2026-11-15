using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Immutable redaction policy shared by <see cref="LoggingInterceptor"/>, <see cref="MonitorInterceptor"/>
    /// and the Editor HTTP Monitor. Redaction is on by default: values of sensitive headers, URL query
    /// values (names are kept) and URL user-info are replaced by <see cref="RedactedValue"/>, including the
    /// URLs carried by <c>Location</c>, <c>Content-Location</c>, <c>Referer</c>, <c>Link</c> and <c>Refresh</c>.
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
        /// Returns <see cref="RedactedValue"/> when <paramref name="headerName"/> is sensitive. Otherwise,
        /// when <see cref="RedactQueryValues"/> is set, the URI parts of URL-valued headers have their
        /// query values and user-info redacted: the whole value of <c>Location</c>, <c>Content-Location</c>
        /// and <c>Referer</c> (RFC 9110 sections 10.2.2, 8.7, 10.1.3), every <c>&lt;...&gt;</c> target of
        /// <c>Link</c> (RFC 8288) and the <c>url=</c> part of <c>Refresh</c>. Any other value is returned
        /// unchanged.
        /// </summary>
        public string RedactHeaderValue(string headerName, string value)
        {
            return IsSensitiveHeader(headerName) ? RedactedValue : RedactUrlValuedHeader(headerName, value);
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

            return UrlRedaction.RedactUrl(url, null, RedactedValue);
        }

        /// <summary>
        /// URL-valued header redaction alone (no sensitive-name check): applied when
        /// <see cref="RedactQueryValues"/> is set.
        /// </summary>
        internal string RedactUrlValuedHeader(string headerName, string value)
        {
            return RedactQueryValues
                ? UrlRedaction.RedactUrlHeaderValue(headerName, value, null, RedactedValue)
                : value;
        }

        /// <summary>
        /// Free-text redaction for timeline values and error messages: every absolute URL in
        /// <paramref name="text"/> has its query values and user-info redacted (when
        /// <see cref="RedactQueryValues"/> is set).
        /// </summary>
        internal string RedactText(string text)
        {
            return RedactQueryValues ? UrlRedaction.RedactUrlsInText(text, RedactedValue) : text;
        }
    }
}
