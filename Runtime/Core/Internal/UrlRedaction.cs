using System;
using System.Text;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// URL redaction shared by the modules that put URLs into logs, timeline events, exception messages,
    /// captures and recordings (Observability, Middleware, Cache, Testing). User-info (RFC 3986 section 3.2.1)
    /// is always replaced; query values are replaced (names kept) for every parameter, or only for the ones a
    /// caller-supplied predicate selects. The fragment is kept.
    /// </summary>
    internal static class UrlRedaction
    {
        internal const string Placeholder = "[REDACTED]";

        /// <summary>
        /// Returns <paramref name="url"/> with user-info and query values replaced by
        /// <paramref name="placeholder"/>. <paramref name="redactQueryName"/> selects the parameters whose
        /// value is replaced (it receives the unescaped name); <c>null</c> replaces every value. Returns the
        /// same instance when nothing needs redacting.
        /// </summary>
        internal static string RedactUrl(
            string url,
            Func<string, bool> redactQueryName = null,
            string placeholder = Placeholder)
        {
            if (string.IsNullOrEmpty(url) || (url.IndexOf('?') < 0 && url.IndexOf('@') < 0))
                return url;

            placeholder ??= Placeholder;
            var fragmentIndex = url.IndexOf('#');
            var beforeFragmentLength = fragmentIndex >= 0 ? fragmentIndex : url.Length;
            var queryIndex = url.IndexOf('?', 0, beforeFragmentLength);
            var beforeQueryLength = queryIndex >= 0 ? queryIndex : beforeFragmentLength;

            var builder = new StringBuilder(url.Length + 16);
            AppendWithoutUserInfo(builder, url, beforeQueryLength, placeholder);
            if (queryIndex >= 0)
            {
                builder.Append('?');
                AppendRedactedQuery(
                    builder,
                    url,
                    queryIndex + 1,
                    beforeFragmentLength,
                    redactQueryName,
                    placeholder);
            }

            if (fragmentIndex >= 0)
                builder.Append(url, fragmentIndex, url.Length - fragmentIndex);

            return builder.ToString();
        }

        /// <summary>
        /// Redacts every absolute URL (<c>scheme://...</c>) embedded in free text such as an exception
        /// message or a timeline value. A URL ends at whitespace, a quote or an angle bracket; the rest of
        /// the text is kept. Relative references are not recognised. Returns the same instance when
        /// nothing needs redacting.
        /// </summary>
        internal static string RedactUrlsInText(string text, string placeholder = Placeholder)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var marker = text.IndexOf("://", StringComparison.Ordinal);
            if (marker < 0)
                return text;

            StringBuilder builder = null;
            var copied = 0;
            while (marker >= 0)
            {
                var start = marker;
                while (start > copied && IsSchemeChar(text[start - 1]))
                    start--;
                while (start < marker && !IsAsciiLetter(text[start]))
                    start++;

                var end = marker + 3;
                while (end < text.Length && !IsUrlTerminator(text[end]))
                    end++;

                if (start < marker)
                {
                    var url = text.Substring(start, end - start);
                    var redacted = RedactUrl(url, null, placeholder);
                    if (!ReferenceEquals(redacted, url))
                    {
                        builder ??= new StringBuilder(text.Length + 16);
                        builder.Append(text, copied, start - copied).Append(redacted);
                        copied = end;
                    }
                }

                marker = end < text.Length ? text.IndexOf("://", end, StringComparison.Ordinal) : -1;
            }

            if (builder == null)
                return text;

            builder.Append(text, copied, text.Length - copied);
            return builder.ToString();
        }

        /// <summary>
        /// Whether a header's value carries a URI: <c>Location</c>, <c>Content-Location</c> (RFC 9110
        /// sections 10.2.2, 8.7), <c>Referer</c> (section 10.1.3), <c>Link</c> (RFC 8288) and the
        /// non-standard <c>Refresh</c>.
        /// </summary>
        internal static bool IsUrlValuedHeader(string headerName)
        {
            if (string.IsNullOrEmpty(headerName))
                return false;

            var name = headerName.Trim();
            return string.Equals(name, "Location", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Content-Location", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Referer", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Link", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "Refresh", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Redacts the URI parts of a URL-valued header (see <see cref="IsUrlValuedHeader"/>): the whole
        /// value of <c>Location</c>, <c>Content-Location</c> and <c>Referer</c>, every <c>&lt;...&gt;</c>
        /// target of <c>Link</c>, and the target of <c>Refresh</c> (with or without the <c>url=</c> prefix). Other headers are returned as is.
        /// </summary>
        internal static string RedactUrlHeaderValue(
            string headerName,
            string value,
            Func<string, bool> redactQueryName = null,
            string placeholder = Placeholder)
        {
            if (string.IsNullOrEmpty(value) || !IsUrlValuedHeader(headerName))
                return value;

            var name = headerName.Trim();
            if (string.Equals(name, "Link", StringComparison.OrdinalIgnoreCase))
                return RedactLinkTargets(value, redactQueryName, placeholder);

            if (string.Equals(name, "Refresh", StringComparison.OrdinalIgnoreCase))
                return RedactRefreshTarget(value, redactQueryName, placeholder);

            return RedactUrl(value, redactQueryName, placeholder);
        }

        /// <summary>
        /// Returns <c>scheme://host[:port]</c> of an absolute URI (no user-info, path, query or fragment),
        /// for messages that must not carry a URL secret. Returns an empty string for <c>null</c> and
        /// <c>"(relative)"</c> for a relative URI.
        /// </summary>
        internal static string Origin(Uri uri)
        {
            if (uri == null)
                return string.Empty;
            if (!uri.IsAbsoluteUri)
                return "(relative)";

            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
        }

        /// <summary>
        /// Redacts the target of a declarative refresh value, parsed like the HTML "shared declarative
        /// refresh steps": <c>time [;|,] [url =] ['|"]target['|"]</c>, where the <c>url=</c> prefix is
        /// optional and may carry whitespace around <c>=</c>. A value that does not start with a time is
        /// redacted as a whole URL (conservative).
        /// </summary>
        private static string RedactRefreshTarget(string value, Func<string, bool> redactQueryName, string placeholder)
        {
            var length = value.Length;
            var i = SkipWhitespace(value, 0);
            var timeStart = i;
            while (i < length && ((value[i] >= '0' && value[i] <= '9') || value[i] == '.'))
                i++;

            if (i == timeStart)
                return RedactUrl(value, redactQueryName, placeholder);

            i = SkipWhitespace(value, i);
            if (i < length && (value[i] == ';' || value[i] == ','))
                i = SkipWhitespace(value, i + 1);

            if (i >= length)
                return value;

            if (length - i >= 3 && string.Compare(value, i, "url", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var afterKeyword = SkipWhitespace(value, i + 3);
                if (afterKeyword < length && value[afterKeyword] == '=')
                    i = SkipWhitespace(value, afterKeyword + 1);
            }

            var targetStart = i;
            var targetEnd = length;
            if (targetStart < length && (value[targetStart] == '\'' || value[targetStart] == '"'))
            {
                var close = value.IndexOf(value[targetStart], targetStart + 1);
                targetStart++;
                if (close >= 0)
                    targetEnd = close;
            }

            var target = value.Substring(targetStart, targetEnd - targetStart);
            var redactedTarget = RedactUrl(target, redactQueryName, placeholder);
            return ReferenceEquals(redactedTarget, target)
                ? value
                : value.Substring(0, targetStart) + redactedTarget + value.Substring(targetEnd);
        }

        private static int SkipWhitespace(string value, int index)
        {
            while (index < value.Length
                   && (value[index] == ' ' || value[index] == '\t' || value[index] == '\n'
                       || value[index] == '\f' || value[index] == '\r'))
                index++;
            return index;
        }

        private static string RedactLinkTargets(string value, Func<string, bool> redactQueryName, string placeholder)
        {
            StringBuilder builder = null;
            var copied = 0;
            var open = value.IndexOf('<');
            while (open >= 0)
            {
                var close = value.IndexOf('>', open + 1);
                if (close < 0)
                    close = value.Length;

                var target = value.Substring(open + 1, close - open - 1);
                var redacted = RedactUrl(target, redactQueryName, placeholder);
                if (!ReferenceEquals(redacted, target))
                {
                    builder ??= new StringBuilder(value.Length + 16);
                    builder.Append(value, copied, open + 1 - copied).Append(redacted);
                    copied = close;
                }

                open = close < value.Length ? value.IndexOf('<', close + 1) : -1;
            }

            if (builder == null)
                return value;

            builder.Append(value, copied, value.Length - copied);
            return builder.ToString();
        }

        private static void AppendWithoutUserInfo(StringBuilder builder, string url, int length, string placeholder)
        {
            int authorityStart;
            if (length >= 2 && url[0] == '/' && url[1] == '/')
            {
                // Network-path reference (RFC 3986 section 4.2): the authority follows the leading "//".
                authorityStart = 2;
            }
            else
            {
                var schemeEnd = IndexOf(url, "://", 0, length);
                if (schemeEnd < 0)
                {
                    builder.Append(url, 0, length);
                    return;
                }

                authorityStart = schemeEnd + 3;
            }

            var authorityEnd = authorityStart < length ? url.IndexOf('/', authorityStart, length - authorityStart) : -1;
            if (authorityEnd < 0)
                authorityEnd = length;

            var at = authorityEnd > authorityStart
                ? url.LastIndexOf('@', authorityEnd - 1, authorityEnd - authorityStart)
                : -1;
            if (at < 0)
            {
                builder.Append(url, 0, length);
                return;
            }

            builder.Append(url, 0, authorityStart)
                .Append(placeholder)
                .Append(url, at, length - at);
        }

        private static void AppendRedactedQuery(
            StringBuilder builder,
            string url,
            int start,
            int end,
            Func<string, bool> redactQueryName,
            string placeholder)
        {
            var pairStart = start;
            while (pairStart <= end)
            {
                var pairEnd = pairStart < end ? url.IndexOf('&', pairStart, end - pairStart) : -1;
                if (pairEnd < 0)
                    pairEnd = end;

                var equals = pairEnd > pairStart ? url.IndexOf('=', pairStart, pairEnd - pairStart) : -1;
                if (equals < 0)
                {
                    builder.Append(url, pairStart, pairEnd - pairStart);
                }
                else if (equals + 1 < pairEnd
                         && (redactQueryName == null
                             || redactQueryName(Unescape(url.Substring(pairStart, equals - pairStart)))))
                {
                    builder.Append(url, pairStart, equals + 1 - pairStart).Append(placeholder);
                }
                else
                {
                    builder.Append(url, pairStart, pairEnd - pairStart);
                }

                if (pairEnd >= end)
                    break;

                builder.Append('&');
                pairStart = pairEnd + 1;
            }
        }

        private static string Unescape(string name)
        {
            try
            {
                return Uri.UnescapeDataString(name.Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return name;
            }
        }

        private static int IndexOf(string text, string value, int start, int length)
        {
            return length - start < value.Length
                ? -1
                : text.IndexOf(value, start, length - start, StringComparison.Ordinal);
        }

        private static bool IsAsciiLetter(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        private static bool IsSchemeChar(char c)
        {
            // RFC 3986 section 3.1: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." )
            return IsAsciiLetter(c) || (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';
        }

        private static bool IsUrlTerminator(char c)
        {
            return char.IsWhiteSpace(c) || c == '"' || c == '\'' || c == '`' || c == '<' || c == '>';
        }
    }
}
