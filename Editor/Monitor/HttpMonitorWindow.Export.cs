using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
            public string text;
            public string base64;
        }

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
                var exportModel = BuildExportModel(evt);
                var json = JsonUtility.ToJson(exportModel, true);
                File.WriteAllText(path, json, Encoding.UTF8);
                Debug.Log($"[TurboHTTP] Exported monitor event to: {path}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TurboHTTP] Failed to export monitor event: {ex.Message}");
            }
        }

        private ExportEventModel BuildExportModel(HttpMonitorEvent evt)
        {
            return new ExportEventModel
            {
                id = evt.Id,
                timestampUtc = evt.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                method = evt.Method,
                url = evt.Url,
                statusCode = evt.StatusCode,
                statusText = evt.StatusText,
                elapsedMilliseconds = evt.ElapsedTime.TotalMilliseconds,
                failureKind = evt.FailureKind.ToString(),
                errorType = evt.ErrorType?.ToString() ?? string.Empty,
                error = evt.Error,
                requestHeaders = ToExportHeaders(evt.RequestHeaders),
                responseHeaders = ToExportHeaders(evt.ResponseHeaders),
                requestBody = ToExportBody(
                    evt.RequestBody,
                    evt.OriginalRequestBodySize,
                    evt.IsRequestBodyTruncated,
                    evt.IsRequestBodyBinary,
                    evt.GetRequestBodyAsString()),
                responseBody = ToExportBody(
                    evt.ResponseBody,
                    evt.OriginalResponseBodySize,
                    evt.IsResponseBodyTruncated,
                    evt.IsResponseBodyBinary,
                    evt.GetResponseBodyAsString()),
                timeline = ToExportTimeline(evt.Timeline)
            };
        }

        private List<ExportHeaderEntry> ToExportHeaders(IReadOnlyDictionary<string, string> headers)
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
                var value = ConfidentialHeaders.Contains(pair.Key)
                    ? "********"
                    : pair.Value ?? string.Empty;
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
            string bodyText)
        {
            return new ExportBodyModel
            {
                capturedSizeBytes = body.Length,
                originalSizeBytes = originalSize,
                truncated = truncated,
                binary = binary,
                text = bodyText ?? string.Empty,
                base64 = body.IsEmpty ? string.Empty : Convert.ToBase64String(body.ToArray())
            };
        }

        private static List<ExportTimelineModel> ToExportTimeline(
            IReadOnlyList<HttpMonitorTimelineEvent> timeline)
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
                    data = ToExportTimelineData(evt.Data)
                });
            }

            return list;
        }

        private static List<ExportHeaderEntry> ToExportTimelineData(
            IReadOnlyDictionary<string, string> data)
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
                    value = sorted[i].Value
                });
            }

            return entries;
        }
    }
}
