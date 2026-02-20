using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    public partial class HttpMonitorWindow
    {
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

                    if (GUILayout.Button("Replay", GUILayout.Width(100f)))
                    {
                        ReplayEvent(_selectedEvent);
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

            // Request line (approximate HTTP/1.1 wire format)
            raw.Append(_selectedEvent.Method);
            raw.Append(' ');
            var url = _selectedEvent.Url ?? "/";
            if (Uri.TryCreate(url, UriKind.Absolute, out var parsed))
                raw.Append(parsed.PathAndQuery);
            else
                raw.Append(url);
            raw.AppendLine(" HTTP/1.1");

            // Host header
            if (parsed != null)
            {
                raw.Append("Host: ");
                raw.AppendLine(parsed.Authority);
            }

            AppendRawHeaders(raw, _selectedEvent.RequestHeaders);
            raw.AppendLine();
            var requestBody = _selectedEvent.GetRequestBodyAsString();
            if (!string.IsNullOrEmpty(requestBody))
            {
                raw.AppendLine(requestBody);
            }
            raw.AppendLine();

            // Response status line
            raw.Append("HTTP/1.1 ");
            raw.Append(_selectedEvent.StatusCode);
            raw.Append(' ');
            raw.AppendLine(_selectedEvent.StatusText);
            AppendRawHeaders(raw, _selectedEvent.ResponseHeaders);
            raw.AppendLine();
            var responseBody = _selectedEvent.GetResponseBodyAsString();
            if (!string.IsNullOrEmpty(responseBody))
            {
                raw.AppendLine(responseBody);
            }

            if (!string.IsNullOrEmpty(_selectedEvent.Error))
            {
                raw.AppendLine();
                raw.AppendLine("--- Error ---");
                raw.AppendLine(_selectedEvent.Error);
            }

            EditorGUILayout.TextArea(raw.ToString(), GUILayout.ExpandHeight(true));
        }

        private void AppendRawHeaders(StringBuilder builder, IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                return;
            }

            foreach (var pair in GetSortedHeaders(headers))
            {
                builder.Append(pair.Key);
                builder.Append(": ");
                builder.AppendLine(FormatHeaderValue(pair.Key, pair.Value));
            }
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
    }
}
