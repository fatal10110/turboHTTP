using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Cache
{
    public sealed partial class CacheInterceptor
    {
        // RFC 9111 section 2: the primary cache key is the target URI. Only equivalences that
        // RFC 3986 sections 6.2.2 (case, percent-encoding, dot segments) and 6.2.3 (default port)
        // define are applied. Empty path segments, empty query parameters and query parameter
        // order are significant to the origin (signed URLs), so they are kept as sent (#72).
        // The fragment is never part of the key (Uri.Query/AbsolutePath exclude it).
        internal static string NormalizeUri(Uri uri)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("Cache key URI must be absolute.", nameof(uri));

            var scheme = NormalizeCaseInsensitiveUriToken(uri.Scheme);
            var host = NormalizeCaseInsensitiveUriToken(uri.Host);
            var portPart = IsDefaultPort(scheme, uri.Port) ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
            var absolutePath = uri.AbsolutePath;
            var path = NeedsPathNormalization(absolutePath) ? NormalizePath(absolutePath) : absolutePath;

            var rawQuery = uri.Query;
            var query = NeedsQueryNormalization(rawQuery)
                ? NormalizePercentEncoding(NormalizeQueryFastPath(rawQuery))
                : NormalizeQueryFastPath(rawQuery);

            var result = new StringBuilder(scheme.Length + host.Length + portPart.Length + path.Length + query.Length + 3);
            result.Append(scheme);
            result.Append("://");
            result.Append(host);
            result.Append(portPart);
            result.Append(path);
            result.Append(query);
            return result.ToString();
        }

        private static bool IsDefaultPort(string scheme, int port)
        {
            return (scheme == "http" && port == 80)
                   || (scheme == "https" && port == 443);
        }

        private static string NormalizeCaseInsensitiveUriToken(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c >= 'A' && c <= 'Z')
                    return value.ToLowerInvariant();
            }

            return value;
        }

        private static bool NeedsPathNormalization(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '/')
                return true;

            if (path.IndexOf('%') >= 0)
                return true;

            if (path.IndexOf("/./", StringComparison.Ordinal) >= 0
                || path.IndexOf("/../", StringComparison.Ordinal) >= 0
                || path.EndsWith("/.", StringComparison.Ordinal)
                || path.EndsWith("/..", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool NeedsQueryNormalization(string query)
        {
            return !string.IsNullOrEmpty(query) && query.IndexOf('%') >= 0;
        }

        // An empty query ("?") is kept: RFC 3986 section 6.2.3 does not license removing the
        // delimiter of an empty component for http(s).
        private static string NormalizeQueryFastPath(string query)
        {
            if (string.IsNullOrEmpty(query))
                return string.Empty;

            if (query[0] == '?')
                return query;

            return "?" + query;
        }

        // RFC 3986 section 5.2.4 remove_dot_segments, then percent-encoding normalization per
        // segment. Only literal "." and ".." are dot segments; an encoded "%2E%2E" stays a
        // segment, so the key never aliases a path the origin may treat literally. Empty
        // segments ("/a//b") are kept: they are not equivalent to "/a/b".
        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return "/";

            var segments = path.Split('/');
            var stack = new List<string>(segments.Length);

            // segments[0] is the empty string before the leading '/'; a rootless path keeps its
            // first segment.
            int first = path[0] == '/' ? 1 : 0;
            for (int i = first; i < segments.Length; i++)
            {
                var segment = segments[i];
                bool isLast = i == segments.Length - 1;

                if (segment == ".")
                {
                    if (isLast)
                        stack.Add(string.Empty);
                    continue;
                }

                if (segment == "..")
                {
                    if (stack.Count > 0)
                        stack.RemoveAt(stack.Count - 1);
                    if (isLast)
                        stack.Add(string.Empty);
                    continue;
                }

                stack.Add(NormalizePercentEncoding(segment));
            }

            return "/" + string.Join("/", stack);
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
    }
}
