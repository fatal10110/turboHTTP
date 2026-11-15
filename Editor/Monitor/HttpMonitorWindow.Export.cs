using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    public partial class HttpMonitorWindow
    {
        [Serializable]
        private sealed class ExportHeaderEntry
        {
            public string key;
            public string value;
        }

        [Serializable]
        private sealed class ExportBodyModel
        {
            public int capturedSizeBytes;
            public int originalSizeBytes;
            public bool truncated;
            public bool binary;
            public bool omitted;
            public string text;
            public string base64;
        }

        private sealed class ExportPolicyCache
        {
            public ExportPolicyCache(RedactionPolicy active, RedactionPolicy export)
            {
                Active = active;
                Export = export;
            }

            public RedactionPolicy Active { get; }
            public RedactionPolicy Export { get; }
        }

        private static ExportPolicyCache _exportPolicyCache;


        [Serializable]
        private sealed class ExportTimelineModel
        {
            public string name;
            public double timestampMilliseconds;
            public List<ExportHeaderEntry> data;
        }

        [Serializable]
        private sealed class ExportEventModel
        {
            public string id;
            public string timestampUtc;
            public string method;
            public string url;
            public int statusCode;
            public string statusText;
            public double elapsedMilliseconds;
            public string failureKind;
            public string errorType;
            public string error;
            public List<ExportHeaderEntry> requestHeaders;
            public List<ExportHeaderEntry> responseHeaders;
            public ExportBodyModel requestBody;
            public ExportBodyModel responseBody;
            public List<ExportTimelineModel> timeline;
        }
        private void ExportEvent(HttpMonitorEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            var path = EditorUtility.SaveFilePanel(
                "Export HTTP Event",
                string.Empty,
                $"http_event_{evt.Id}",
                "json");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                var json = BuildExportJson(
                    evt,
                    MonitorInterceptor.RedactionPolicy,
                    TurboHttpSettings.IncludeBodiesInExport);
                File.WriteAllText(path, json, Encoding.UTF8);
                Debug.Log($"[TurboHTTP] Exported monitor event to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TurboHTTP] Failed to export monitor event: {ex.Message}");
            }
        }

        /// <summary>
        /// The JSON written by Export. Every string passes through <see cref="ResolveExportPolicy"/> of the
        /// capture policy (URL, error text, headers, timeline values); bodies are omitted unless
        /// <paramref name="includeBodies"/> is set, because body content is not redacted (#1009).
        /// </summary>
        internal static string BuildExportJson(HttpMonitorEvent evt, RedactionPolicy activePolicy, bool includeBodies)
        {
            if (evt == null)
            {
                throw new ArgumentNullException(nameof(evt));
            }

            return JsonUtility.ToJson(BuildExportModel(evt, ResolveExportPolicy(activePolicy), includeBodies), true);
        }

        /// <summary>
        /// The policy the window display (when masking is on) and every export apply: the active capture
        /// policy with <see cref="RedactionPolicy.DefaultSensitiveHeaderNames"/> and URL query-value redaction
        /// always added, so custom sensitive headers stay masked and defaults cannot be dropped from an export.
        /// </summary>
        internal static RedactionPolicy ResolveExportPolicy(RedactionPolicy activePolicy)
        {
            activePolicy ??= RedactionPolicy.Default;
            var cached = Volatile.Read(ref _exportPolicyCache);
            if (cached != null && ReferenceEquals(cached.Active, activePolicy))
            {
                return cached.Export;
            }

            var defaults = RedactionPolicy.DefaultSensitiveHeaderNames;
            var names = new string[defaults.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = defaults[i];
            }

            var export = activePolicy.WithSensitiveHeaders(names).WithQueryValueRedaction(true);
            Volatile.Write(ref _exportPolicyCache, new ExportPolicyCache(activePolicy, export));
            return export;
        }

        private static ExportEventModel BuildExportModel(HttpMonitorEvent evt, RedactionPolicy policy, bool includeBodies)
        {
            return new ExportEventModel
            {
                id = evt.Id,
                timestampUtc = evt.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                method = evt.Method,
                url = policy.RedactUrl(evt.Url),
                statusCode = evt.StatusCode,
                statusText = evt.StatusText,
                elapsedMilliseconds = evt.ElapsedTime.TotalMilliseconds,
                failureKind = evt.FailureKind.ToString(),
                errorType = evt.ErrorType?.ToString() ?? string.Empty,
                error = policy.RedactText(evt.Error),
                requestHeaders = ToExportHeaders(evt.RequestHeaders, policy),
                responseHeaders = ToExportHeaders(evt.ResponseHeaders, policy),
                requestBody = ToExportBody(
                    evt.RequestBody,
                    evt.OriginalRequestBodySize,
                    evt.IsRequestBodyTruncated,
                    evt.IsRequestBodyBinary,
                    includeBodies ? evt.GetRequestBodyAsString() : null,
                    includeBodies),
                responseBody = ToExportBody(
                    evt.ResponseBody,
                    evt.OriginalResponseBodySize,
                    evt.IsResponseBodyTruncated,
                    evt.IsResponseBodyBinary,
                    includeBodies ? evt.GetResponseBodyAsString() : null,
                    includeBodies),
                timeline = ToExportTimeline(evt.Timeline, policy)
            };
        }

        private static List<ExportHeaderEntry> ToExportHeaders(
            IReadOnlyDictionary<string, string> headers,
            RedactionPolicy policy)
        {
            var list = new List<ExportHeaderEntry>();
            if (headers == null || headers.Count == 0)
            {
                return list;
            }

            foreach (var pair in GetSortedHeaders(headers))
            {
                // Always mask confidential headers in exports to prevent credential
                // leakage when files are shared or committed to version control.
                var value = policy.RedactHeaderValue(pair.Key, pair.Value ?? string.Empty);
                list.Add(new ExportHeaderEntry
                {
                    key = pair.Key,
                    value = value
                });
            }

            return list;
        }

        private static ExportBodyModel ToExportBody(
            ReadOnlyMemory<byte> body,
            int originalSize,
            bool truncated,
            bool binary,
            string bodyText,
            bool includeBody)
        {
            return new ExportBodyModel
            {
                capturedSizeBytes = body.Length,
                originalSizeBytes = originalSize,
                truncated = truncated,
                binary = binary,
                omitted = !includeBody && !body.IsEmpty,
                text = includeBody ? bodyText ?? string.Empty : string.Empty,
                base64 = !includeBody || body.IsEmpty ? string.Empty : Convert.ToBase64String(body.ToArray())
            };
        }

        private static List<ExportTimelineModel> ToExportTimeline(
            IReadOnlyList<HttpMonitorTimelineEvent> timeline,
            RedactionPolicy policy)
        {
            var list = new List<ExportTimelineModel>();
            if (timeline == null || timeline.Count == 0)
            {
                return list;
            }

            for (int i = 0; i < timeline.Count; i++)
            {
                var evt = timeline[i];
                if (evt == null)
                {
                    continue;
                }

                list.Add(new ExportTimelineModel
                {
                    name = evt.Name,
                    timestampMilliseconds = evt.Timestamp.TotalMilliseconds,
                    data = ToExportTimelineData(evt.Data, policy)
                });
            }

            return list;
        }

        private static List<ExportHeaderEntry> ToExportTimelineData(
            IReadOnlyDictionary<string, string> data,
            RedactionPolicy policy)
        {
            var entries = new List<ExportHeaderEntry>();
            if (data == null || data.Count == 0)
            {
                return entries;
            }

            var sorted = new List<KeyValuePair<string, string>>(data.Count);
            foreach (var pair in data)
            {
                sorted.Add(pair);
            }

            sorted.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
            for (int i = 0; i < sorted.Count; i++)
            {
                entries.Add(new ExportHeaderEntry
                {
                    key = sorted[i].Key,
                    value = MonitorInterceptor.RedactTimelineValue(policy, sorted[i].Key, sorted[i].Value)
                });
            }

            return entries;
        }
    }
}
