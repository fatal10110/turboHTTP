using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed partial class RecordReplayTransport
    {
        private string BuildRequestKey(UHttpRequest request)
        {
            var normalizedUrl = NormalizeUriForKey(request.Uri);
            var bodyHash = ComputeBodyHash(request.Body);
            return BuildRequestKey(request.Method.ToUpperString(), normalizedUrl, request.Headers, bodyHash);
        }

        private string BuildRequestKeyFromEntry(RecordingEntryDto entry)
        {
            var keyHeaders = entry.RequestHeaders ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            return BuildRequestKey(
                entry.Method,
                entry.Url,
                keyHeaders,
                entry.RequestBodyHash ?? "sha256:empty");
        }

        private string BuildRequestKey(string method, string normalizedUrl, HttpHeaders headers, string bodyHash)
        {
            return BuildRequestKey(method, normalizedUrl, ToDictionary(headers, redact: false), bodyHash);
        }

        private string BuildRequestKey(
            string method,
            string normalizedUrl,
            Dictionary<string, List<string>> headers,
            string bodyHash)
        {
            var normalizedMethod = (method ?? string.Empty).Trim().ToUpperInvariant();
            var headerSignature = BuildHeaderSignature(headers);
            return string.Concat(
                normalizedMethod,
                "|",
                normalizedUrl ?? string.Empty,
                "|",
                headerSignature,
                "|",
                bodyHash ?? "sha256:empty");
        }

        private static string BuildRelaxedKey(string method, string normalizedUrl)
        {
            return string.Concat(
                (method ?? string.Empty).Trim().ToUpperInvariant(),
                "|",
                normalizedUrl ?? string.Empty);
        }

        private string BuildHeaderSignature(Dictionary<string, List<string>> headers)
        {
            if (headers == null || headers.Count == 0)
                return string.Empty;

            var builder = new StringBuilder(128);
            var names = headers.Keys
                .Where(ShouldIncludeHeaderInMatchKey)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

            foreach (var name in names)
            {
                if (!headers.TryGetValue(name, out var values) || values == null || values.Count == 0)
                    continue;

                builder.Append(name.ToLowerInvariant());
                builder.Append('=');
                var sortedValues = values.OrderBy(v => v ?? string.Empty, StringComparer.Ordinal).ToArray();
                for (int i = 0; i < sortedValues.Length; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(sortedValues[i] ?? string.Empty);
                }
                builder.Append(';');
            }

            return builder.ToString();
        }

        private bool ShouldIncludeHeaderInMatchKey(string headerName)
        {
            if (string.IsNullOrWhiteSpace(headerName))
                return false;
            if (_excludedMatchHeaders.Contains(headerName))
                return false;
            return _matchHeaders.Contains(headerName);
        }

        private string NormalizeUriForStorage(Uri uri)
        {
            return NormalizeUri(uri, redactSensitiveQueryValues: true);
        }

        private string NormalizeUriForKey(Uri uri)
        {
            return NormalizeUri(uri, redactSensitiveQueryValues: true);
        }

        private string NormalizeUri(Uri uri, bool redactSensitiveQueryValues)
        {
            if (uri == null)
                return string.Empty;

            var scheme = uri.Scheme.ToLowerInvariant();
            var host = uri.Host.ToLowerInvariant();
            var defaultPort = (scheme == Uri.UriSchemeHttp && uri.Port == 80) ||
                              (scheme == Uri.UriSchemeHttps && uri.Port == 443);
            var authority = defaultPort
                ? host
                : host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

            var path = string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath;
            var normalizedQuery = NormalizeQuery(uri.Query, redactSensitiveQueryValues);

            return string.Concat(
                scheme,
                "://",
                authority,
                path,
                normalizedQuery);
        }

        private string NormalizeQuery(string query, bool redactSensitiveQueryValues)
        {
            if (string.IsNullOrEmpty(query))
                return string.Empty;

            var trimmed = query[0] == '?' ? query.Substring(1) : query;
            if (trimmed.Length == 0)
                return string.Empty;

            var items = new List<KeyValuePair<string, string>>();
            var segments = trimmed.Split('&');
            for (int i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];
                if (string.IsNullOrEmpty(segment))
                    continue;

                var equalsIndex = segment.IndexOf('=');
                string key;
                string value;
                if (equalsIndex < 0)
                {
                    key = segment;
                    value = string.Empty;
                }
                else
                {
                    key = segment.Substring(0, equalsIndex);
                    value = segment.Substring(equalsIndex + 1);
                }

                if (redactSensitiveQueryValues && _redactionPolicy.ShouldRedactQueryParameter(Uri.UnescapeDataString(key)))
                {
                    value = _redactionPolicy.RedactedValue;
                }

                items.Add(new KeyValuePair<string, string>(key, value));
            }

            items.Sort((a, b) =>
            {
                var keyCompare = StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
                if (keyCompare != 0) return keyCompare;
                return StringComparer.Ordinal.Compare(a.Value, b.Value);
            });

            if (items.Count == 0)
                return string.Empty;

            var builder = new StringBuilder(64);
            builder.Append('?');
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) builder.Append('&');
                builder.Append(items[i].Key);
                if (!string.IsNullOrEmpty(items[i].Value))
                {
                    builder.Append('=');
                    builder.Append(items[i].Value);
                }
            }

            return builder.ToString();
        }
    }
}
