using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    using TurboHTTP.Cache;

    public sealed partial class CacheMiddleware
    {
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
                ? NormalizeQuery(rawQuery)
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

            if (path.IndexOf("//", StringComparison.Ordinal) >= 0)
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
            if (string.IsNullOrEmpty(query) || query == "?")
                return false;

            int start = query[0] == '?' ? 1 : 0;
            for (int i = start; i < query.Length; i++)
            {
                var c = query[i];
                if (c == '&' || c == '%')
                    return true;
            }

            return false;
        }

        private static string NormalizeQueryFastPath(string query)
        {
            if (string.IsNullOrEmpty(query) || query == "?")
                return string.Empty;

            if (query[0] == '?')
                return query;

            return "?" + query;
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
