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
    /// <summary>
    /// Editor window for browsing captured HTTP monitor events.
    /// </summary>
    public class HttpMonitorWindow : EditorWindow
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

        private const double MinRepaintIntervalSeconds = 0.1d;

        private static readonly HashSet<string> ConfidentialHeaders = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "X-Auth-Token",
            "X-API-Key",
            "Cookie",
            "Set-Cookie"
        };

        public static bool DefaultMaskConfidentialHeaders { get; set; }

        private readonly List<HttpMonitorEvent> _historyCache = new List<HttpMonitorEvent>(1024);
        private readonly List<HttpMonitorEvent> _filteredCache = new List<HttpMonitorEvent>(1024);
        private readonly string[] _tabNames = { "Request", "Response", "Timeline", "Raw" };

        private Vector2 _requestListScroll;
        private Vector2 _detailsScroll;
        private HttpMonitorEvent _selectedEvent;
        private string _filterUrl = string.Empty;
        private string _filterMethod = string.Empty;
        private string _filterStatus = string.Empty;
        private bool _maskConfidentialHeaders;
        private bool _historyDirty = true;
        private bool _filterDirty = true;
        private int _selectedTab;
        private int _pendingUiRefresh;
        private bool _deferredRepaintScheduled;
        private double _lastRepaintTime;

        [MenuItem("Window/TurboHTTP/HTTP Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<HttpMonitorWindow>("HTTP Monitor");
            window.minSize = new Vector2(900f, 420f);
        }

        private void OnEnable()
        {
            _maskConfidentialHeaders = DefaultMaskConfidentialHeaders;
            MonitorMiddleware.OnRequestCaptured += OnRequestCaptured;
            _historyDirty = true;
            _filterDirty = true;
            RefreshHistoryCacheIfDirty();
        }

        private void OnDisable()
        {
            MonitorMiddleware.OnRequestCaptured -= OnRequestCaptured;
            EditorApplication.update -= DeferredRepaint;
            _deferredRepaintScheduled = false;
            Interlocked.Exchange(ref _pendingUiRefresh, 0);
        }

        private void OnRequestCaptured(HttpMonitorEvent _)
        {
            if (Interlocked.Exchange(ref _pendingUiRefresh, 1) == 1)
            {
                return;
            }

            EditorApplication.delayCall += FlushCaptureOnMainThread;
        }

        private void FlushCaptureOnMainThread()
        {
            if (this == null)
            {
                return;
            }

            Interlocked.Exchange(ref _pendingUiRefresh, 0);
            _historyDirty = true;
            _filterDirty = true;
            ScheduleThrottledRepaint();
        }

        private void ScheduleThrottledRepaint()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaintTime >= MinRepaintIntervalSeconds)
            {
                _lastRepaintTime = now;
                Repaint();
                return;
            }

            if (_deferredRepaintScheduled)
            {
                return;
            }

            _deferredRepaintScheduled = true;
            EditorApplication.update += DeferredRepaint;
        }

        private void DeferredRepaint()
        {
            if (this == null)
            {
                EditorApplication.update -= DeferredRepaint;
                _deferredRepaintScheduled = false;
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now - _lastRepaintTime < MinRepaintIntervalSeconds)
            {
                return;
            }

            EditorApplication.update -= DeferredRepaint;
            _deferredRepaintScheduled = false;
            _lastRepaintTime = now;
            Repaint();
        }

        private void OnGUI()
        {
            RefreshHistoryCacheIfDirty();
            BuildFilteredCacheIfNeeded();

            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            DrawRequestList();
            DrawDetailsPanel();
            EditorGUILayout.EndHorizontal();
        }

        private void RefreshHistoryCacheIfDirty()
        {
            if (!_historyDirty)
            {
                return;
            }

            MonitorMiddleware.GetHistorySnapshot(_historyCache);
            _historyDirty = false;
            _filterDirty = true;

            if (_selectedEvent != null && !ContainsById(_historyCache, _selectedEvent.Id))
            {
                _selectedEvent = null;
            }
        }

        private static bool ContainsById(List<HttpMonitorEvent> events, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }

            for (int i = 0; i < events.Count; i++)
            {
                var evt = events[i];
                if (evt != null && string.Equals(evt.Id, id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(64f)))
                {
                    MonitorMiddleware.ClearHistory();
                    _selectedEvent = null;
                    _historyDirty = true;
                    _filterDirty = true;
                }

                GUILayout.Space(6f);
                var newMaskValue = GUILayout.Toggle(
                    _maskConfidentialHeaders,
                    "Mask Confidential Headers",
                    EditorStyles.toolbarButton,
                    GUILayout.Width(180f));
                if (newMaskValue != _maskConfidentialHeaders)
                {
                    _maskConfidentialHeaders = newMaskValue;
                }

                GUILayout.Space(8f);
                GUILayout.Label("URL", GUILayout.Width(24f));
                var newFilterUrl = GUILayout.TextField(_filterUrl, EditorStyles.toolbarTextField, GUILayout.Width(230f));
                if (!string.Equals(newFilterUrl, _filterUrl, StringComparison.Ordinal))
                {
                    _filterUrl = newFilterUrl;
                    _filterDirty = true;
                }

                GUILayout.Space(4f);
                GUILayout.Label("Method", GUILayout.Width(46f));
                var newFilterMethod = GUILayout.TextField(_filterMethod, EditorStyles.toolbarTextField, GUILayout.Width(74f));
                if (!string.Equals(newFilterMethod, _filterMethod, StringComparison.Ordinal))
                {
                    _filterMethod = newFilterMethod;
                    _filterDirty = true;
                }

                GUILayout.Space(4f);
                GUILayout.Label("Status", GUILayout.Width(40f));
                var newFilterStatus = GUILayout.TextField(_filterStatus, EditorStyles.toolbarTextField, GUILayout.Width(56f));
                if (!string.Equals(newFilterStatus, _filterStatus, StringComparison.Ordinal))
                {
                    _filterStatus = newFilterStatus;
                    _filterDirty = true;
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label(
                    $"Shown {_filteredCache.Count}/{_historyCache.Count}",
                    EditorStyles.miniLabel,
                    GUILayout.Width(120f));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRequestList()
        {
            var listWidth = Mathf.Max(340f, position.width * 0.42f);

            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));
            {
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                GUILayout.Label("Method", GUILayout.Width(62f));
                GUILayout.Label("URL", GUILayout.ExpandWidth(true));
                GUILayout.Label("Status", GUILayout.Width(48f));
                GUILayout.Label("Time", GUILayout.Width(54f));
                EditorGUILayout.EndHorizontal();

                _requestListScroll = EditorGUILayout.BeginScrollView(_requestListScroll);
                {
                    for (int i = 0; i < _filteredCache.Count; i++)
                    {
                        DrawRequestRow(_filteredCache[i]);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawRequestRow(HttpMonitorEvent evt)
        {
            if (evt == null)
            {
                return;
            }

            var isSelected = _selectedEvent != null
                && string.Equals(_selectedEvent.Id, evt.Id, StringComparison.Ordinal);
            var selectedColor = new Color(0.25f, 0.45f, 0.75f, 0.45f);
            var originalBg = GUI.backgroundColor;
            if (isSelected)
            {
                GUI.backgroundColor = selectedColor;
            }

            EditorGUILayout.BeginHorizontal("box", GUILayout.Height(22f));
            {
                GUI.backgroundColor = originalBg;

                var originalColor = GUI.color;
                GUI.color = GetMethodColor(evt.Method);
                GUILayout.Label(evt.Method ?? string.Empty, GUILayout.Width(62f));
                GUI.color = originalColor;

                var url = evt.Url ?? string.Empty;
                var urlLabel = url.Length > 72 ? url.Substring(0, 69) + "..." : url;
                GUILayout.Label(urlLabel, GUILayout.ExpandWidth(true));

                GUI.color = GetStatusColor(evt.StatusCode);
                GUILayout.Label(
                    evt.StatusCode > 0
                        ? evt.StatusCode.ToString(CultureInfo.InvariantCulture)
                        : "-",
                    GUILayout.Width(48f));
                GUI.color = originalColor;

                GUILayout.Label(
                    $"{evt.ElapsedTime.TotalMilliseconds:F0}ms",
                    GUILayout.Width(54f));
            }
            EditorGUILayout.EndHorizontal();

            var rowRect = GUILayoutUtility.GetLastRect();
            if (Event.current.type == EventType.MouseDown && rowRect.Contains(Event.current.mousePosition))
            {
                _selectedEvent = evt;
                _selectedTab = 0;
                Event.current.Use();
                Repaint();
            }
        }

        private void DrawDetailsPanel()
        {
            EditorGUILayout.BeginVertical();
            {
                if (_selectedEvent == null)
                {
                    EditorGUILayout.HelpBox("Select a request to view details.", MessageType.Info);
                    return;
                }

                _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);

                _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll);
                {
                    switch (_selectedTab)
                    {
                        case 0:
                            DrawRequestTab();
                            break;
                        case 1:
                            DrawResponseTab();
                            break;
                        case 2:
                            DrawTimelineTab();
                            break;
                        case 3:
                            DrawRawTab();
                            break;
                    }
                }
                EditorGUILayout.EndScrollView();

                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Copy URL", GUILayout.Width(100f)))
                    {
                        EditorGUIUtility.systemCopyBuffer = _selectedEvent.Url ?? string.Empty;
                    }

                    if (GUILayout.Button("Export", GUILayout.Width(100f)))
                    {
                        ExportEvent(_selectedEvent);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawRequestTab()
        {
            EditorGUILayout.LabelField("Request", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Method", _selectedEvent.Method ?? string.Empty);
            EditorGUILayout.LabelField("URL", _selectedEvent.Url ?? string.Empty);
            DrawBodyMeta(
                _selectedEvent.OriginalRequestBodySize,
                _selectedEvent.RequestBody.Length,
                _selectedEvent.IsRequestBodyTruncated,
                _selectedEvent.IsRequestBodyBinary);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Headers", EditorStyles.boldLabel);
            DrawHeaders(_selectedEvent.RequestHeaders);

            if (!_selectedEvent.RequestBody.IsEmpty)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Body", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(_selectedEvent.GetRequestBodyAsString(), GUILayout.MinHeight(120f));
            }
        }

        private void DrawResponseTab()
        {
            EditorGUILayout.LabelField("Response", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            var statusLabel = _selectedEvent.StatusCode > 0
                ? $"{_selectedEvent.StatusCode} {_selectedEvent.StatusText}"
                : "No response";
            EditorGUILayout.LabelField("Status", statusLabel);
            EditorGUILayout.LabelField("Elapsed", $"{_selectedEvent.ElapsedTime.TotalMilliseconds:F2}ms");
            EditorGUILayout.LabelField("Failure Kind", _selectedEvent.FailureKind.ToString());
            if (_selectedEvent.ErrorType.HasValue)
            {
                EditorGUILayout.LabelField("Error Type", _selectedEvent.ErrorType.Value.ToString());
            }

            DrawBodyMeta(
                _selectedEvent.OriginalResponseBodySize,
                _selectedEvent.ResponseBody.Length,
                _selectedEvent.IsResponseBodyTruncated,
                _selectedEvent.IsResponseBodyBinary);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Headers", EditorStyles.boldLabel);
            DrawHeaders(_selectedEvent.ResponseHeaders);

            if (!_selectedEvent.ResponseBody.IsEmpty)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Body", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(_selectedEvent.GetResponseBodyAsString(), GUILayout.MinHeight(180f));
            }

            if (!string.IsNullOrEmpty(_selectedEvent.Error))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_selectedEvent.Error, MessageType.Error);
            }
        }

        private void DrawTimelineTab()
        {
            EditorGUILayout.LabelField("Timeline", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            var timeline = _selectedEvent.Timeline;
            if (timeline == null || timeline.Count == 0)
            {
                EditorGUILayout.LabelField("<none>");
            }
            else
            {
                for (int i = 0; i < timeline.Count; i++)
                {
                    var evt = timeline[i];
                    if (evt == null)
                    {
                        continue;
                    }

                    EditorGUILayout.LabelField(
                        $"{evt.Timestamp.TotalMilliseconds,8:F2} ms   {evt.Name}");

                    if (evt.Data == null || evt.Data.Count == 0)
                    {
                        continue;
                    }

                    EditorGUI.indentLevel++;
                    foreach (var pair in evt.Data)
                    {
                        EditorGUILayout.LabelField(pair.Key, pair.Value);
                    }
                    EditorGUI.indentLevel--;
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Total", $"{_selectedEvent.ElapsedTime.TotalMilliseconds:F2}ms");
        }

        private void DrawRawTab()
        {
            var raw = new StringBuilder(4096);
            raw.AppendLine("=== REQUEST ===");
            raw.Append(_selectedEvent.Method);
            raw.Append(' ');
            raw.AppendLine(_selectedEvent.Url);
            raw.AppendLine();
            raw.AppendLine("Headers:");
            AppendHeaders(raw, _selectedEvent.RequestHeaders);
            raw.AppendLine();
            raw.AppendLine("Body:");
            raw.AppendLine(_selectedEvent.GetRequestBodyAsString());
            raw.AppendLine();
            raw.AppendLine("=== RESPONSE ===");
            raw.Append("Status: ");
            raw.Append(_selectedEvent.StatusCode);
            raw.Append(' ');
            raw.AppendLine(_selectedEvent.StatusText);
            raw.AppendLine();
            raw.AppendLine("Headers:");
            AppendHeaders(raw, _selectedEvent.ResponseHeaders);
            raw.AppendLine();
            raw.AppendLine("Body:");
            raw.AppendLine(_selectedEvent.GetResponseBodyAsString());

            if (!string.IsNullOrEmpty(_selectedEvent.Error))
            {
                raw.AppendLine();
                raw.AppendLine("Error:");
                raw.AppendLine(_selectedEvent.Error);
            }

            EditorGUILayout.TextArea(raw.ToString(), GUILayout.ExpandHeight(true));
        }

        private void BuildFilteredCacheIfNeeded()
        {
            if (!_filterDirty)
            {
                return;
            }

            _filteredCache.Clear();
            for (int i = _historyCache.Count - 1; i >= 0; i--)
            {
                var evt = _historyCache[i];
                if (MatchesFilter(evt))
                {
                    _filteredCache.Add(evt);
                }
            }

            _filterDirty = false;
        }

        private bool MatchesFilter(HttpMonitorEvent evt)
        {
            if (evt == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterUrl)
                && (evt.Url == null
                    || evt.Url.IndexOf(_filterUrl, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterMethod)
                && (evt.Method == null
                    || evt.Method.IndexOf(_filterMethod, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterStatus))
            {
                var statusValue = evt.StatusCode.ToString(CultureInfo.InvariantCulture);
                if (statusValue.IndexOf(_filterStatus, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        private void DrawBodyMeta(
            int originalSize,
            int capturedSize,
            bool truncated,
            bool binary)
        {
            var label = $"{capturedSize}/{Math.Max(0, originalSize)} bytes";
            if (binary)
            {
                label += " (binary)";
            }

            if (truncated)
            {
                label += " - preview/truncated";
            }

            EditorGUILayout.LabelField("Captured", label);
        }

        private void DrawHeaders(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                EditorGUILayout.LabelField("<none>");
                return;
            }

            foreach (var pair in GetSortedHeaders(headers))
            {
                EditorGUILayout.LabelField(pair.Key, FormatHeaderValue(pair.Key, pair.Value));
            }
        }

        private void AppendHeaders(StringBuilder builder, IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                builder.AppendLine("<none>");
                return;
            }

            foreach (var pair in GetSortedHeaders(headers))
            {
                builder.Append(pair.Key);
                builder.Append(": ");
                builder.AppendLine(FormatHeaderValue(pair.Key, pair.Value));
            }
        }

        private IEnumerable<KeyValuePair<string, string>> GetSortedHeaders(
            IReadOnlyDictionary<string, string> headers)
        {
            var entries = new List<KeyValuePair<string, string>>(headers.Count);
            foreach (var pair in headers)
            {
                entries.Add(pair);
            }

            entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            return entries;
        }

        private string FormatHeaderValue(string key, string value)
        {
            if (_maskConfidentialHeaders && !string.IsNullOrEmpty(key) && ConfidentialHeaders.Contains(key))
            {
                return "********";
            }

            return value ?? string.Empty;
        }

        private static Color GetMethodColor(string method)
        {
            switch (method)
            {
                case "GET":
                    return new Color(0.3f, 0.75f, 0.35f);
                case "POST":
                    return new Color(0.3f, 0.55f, 0.9f);
                case "PUT":
                    return new Color(0.9f, 0.75f, 0.2f);
                case "DELETE":
                    return new Color(0.9f, 0.3f, 0.3f);
                default:
                    return Color.white;
            }
        }

        private static Color GetStatusColor(int statusCode)
        {
            if (statusCode >= 200 && statusCode < 300)
            {
                return new Color(0.3f, 0.8f, 0.35f);
            }

            if (statusCode >= 300 && statusCode < 400)
            {
                return new Color(0.35f, 0.72f, 0.92f);
            }

            if (statusCode >= 400 && statusCode < 500)
            {
                return new Color(0.95f, 0.74f, 0.2f);
            }

            if (statusCode >= 500)
            {
                return new Color(0.92f, 0.35f, 0.3f);
            }

            return Color.white;
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
                list.Add(new ExportHeaderEntry
                {
                    key = pair.Key,
                    value = FormatHeaderValue(pair.Key, pair.Value)
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
