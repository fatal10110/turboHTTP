# Phase 12: Editor Tooling

**Milestone:** M3 (v1.0 "feature-complete + release")
**Dependencies:** Phase 11 (Unity Integration)
**Estimated Complexity:** Medium
**Critical:** High - Major differentiator

## Overview

Implement the HTTP Monitor window - a key differentiator that allows developers to inspect all HTTP traffic in real-time within the Unity Editor. This tool dramatically improves debugging and development experience.

Detailed sub-phase breakdown: [Phase 12 Implementation Plan - Overview](phase12/overview.md)

## Goals

1. Create HTTP Monitor EditorWindow
2. Display all requests/responses in real-time
3. Show request details (headers, body, timeline)
4. Show response details (status, headers, body)
5. Filter requests by URL, status, method
6. Export request/response data
7. Replay requests
8. Clear history

## Tasks

### Task 12.1: Monitor Data Model

**File:** `Runtime/Observability/HttpMonitorEvent.cs`

```csharp
using System;
using System.Collections.Generic;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Represents a captured HTTP request/response for monitoring.
    /// </summary>
    [Serializable]
    public class HttpMonitorEvent
    {
        public string Id { get; set; }
        public DateTime Timestamp { get; set; }

        // Request
        public string Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> RequestHeaders { get; set; }
        public byte[] RequestBody { get; set; }

        // Response
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public Dictionary<string, string> ResponseHeaders { get; set; }
        public byte[] ResponseBody { get; set; }

        // Timing
        public TimeSpan ElapsedTime { get; set; }
        public List<TimelineEvent> Timeline { get; set; }

        // Error
        public string Error { get; set; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
        public bool IsError => !string.IsNullOrEmpty(Error);

        public string GetRequestBodyAsString()
        {
            if (RequestBody == null || RequestBody.Length == 0)
                return string.Empty;
            return System.Text.Encoding.UTF8.GetString(RequestBody);
        }

        public string GetResponseBodyAsString()
        {
            if (ResponseBody == null || ResponseBody.Length == 0)
                return string.Empty;
            return System.Text.Encoding.UTF8.GetString(ResponseBody);
        }
    }
}
```

### Task 12.2: Monitor Collector Middleware

**File:** `Runtime/Observability/MonitorMiddleware.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Middleware that captures HTTP traffic for the monitor window.
    /// </summary>
    public class MonitorMiddleware : IHttpMiddleware
    {
        public static event Action<HttpMonitorEvent> OnRequestCaptured;
        private static readonly List<HttpMonitorEvent> _history = new List<HttpMonitorEvent>();
        private static readonly object _lock = new object();

        public static IReadOnlyList<HttpMonitorEvent> History
        {
            get
            {
                lock (_lock)
                {
                    return _history.ToList();
                }
            }
        }

        public static void ClearHistory()
        {
            lock (_lock)
            {
                _history.Clear();
            }
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var monitorEvent = new HttpMonitorEvent
            {
                Id = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow,
                Method = request.Method.ToString(),
                Url = request.Uri.ToString(),
                RequestHeaders = request.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                RequestBody = request.Body
            };

            UHttpResponse response = null;
            Exception exception = null;

            try
            {
                response = await next(request, context, cancellationToken);
                return response;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                // Capture response data
                if (response != null)
                {
                    monitorEvent.StatusCode = (int)response.StatusCode;
                    monitorEvent.StatusText = response.StatusCode.ToString();
                    monitorEvent.ResponseHeaders = response.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                    monitorEvent.ResponseBody = response.Body;

                    if (response.Error != null)
                    {
                        monitorEvent.Error = response.Error.ToString();
                    }
                }
                else if (exception != null)
                {
                    monitorEvent.Error = exception.Message;
                }

                monitorEvent.ElapsedTime = context.Elapsed;
                monitorEvent.Timeline = context.Timeline.ToList();

                // Store in history
                lock (_lock)
                {
                    _history.Add(monitorEvent);

                    // Limit history size
                    if (_history.Count > 1000)
                    {
                        _history.RemoveAt(0);
                    }
                }

                // Notify listeners
                OnRequestCaptured?.Invoke(monitorEvent);
            }
        }
    }
}
```

### Task 12.3: HTTP Monitor Window

**File:** `Editor/Monitor/HttpMonitorWindow.cs`

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Editor window for monitoring HTTP traffic.
    /// </summary>
    public class HttpMonitorWindow : EditorWindow
    {
        private Vector2 _requestListScroll;
        private Vector2 _detailsScroll;
        private HttpMonitorEvent _selectedEvent;
        private string _filterUrl = "";
        private string _filterMethod = "";
        private int _selectedTab = 0;
        private readonly string[] _tabNames = { "Request", "Response", "Timeline", "Raw" };

        [MenuItem("Window/TurboHTTP/HTTP Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<HttpMonitorWindow>("HTTP Monitor");
            window.minSize = new Vector2(800, 400);
        }

        private void OnEnable()
        {
            MonitorMiddleware.OnRequestCaptured += OnRequestCaptured;
        }

        private void OnDisable()
        {
            MonitorMiddleware.OnRequestCaptured -= OnRequestCaptured;
        }

        private void OnRequestCaptured(HttpMonitorEvent evt)
        {
            Repaint();
        }

        private void OnGUI()
        {
            DrawToolbar();

            EditorGUILayout.BeginHorizontal();
            {
                // Left panel - request list
                DrawRequestList();

                // Right panel - details
                if (_selectedEvent != null)
                {
                    DrawDetails();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    MonitorMiddleware.ClearHistory();
                    _selectedEvent = null;
                }

                GUILayout.FlexibleSpace();

                GUILayout.Label("Filter:", GUILayout.Width(40));
                _filterUrl = GUILayout.TextField(_filterUrl, EditorStyles.toolbarSearchField, GUILayout.Width(200));

                GUILayout.Label("Method:", GUILayout.Width(50));
                _filterMethod = GUILayout.TextField(_filterMethod, EditorStyles.toolbarSearchField, GUILayout.Width(60));

                var history = MonitorMiddleware.History;
                GUILayout.Label($"Total: {history.Count}", GUILayout.Width(80));
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawRequestList()
        {
            var listWidth = position.width * 0.4f;

            EditorGUILayout.BeginVertical(GUILayout.Width(listWidth));
            {
                // Header
                EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
                {
                    GUILayout.Label("Method", GUILayout.Width(60));
                    GUILayout.Label("URL", GUILayout.ExpandWidth(true));
                    GUILayout.Label("Status", GUILayout.Width(50));
                    GUILayout.Label("Time", GUILayout.Width(60));
                }
                EditorGUILayout.EndHorizontal();

                // Request list
                _requestListScroll = EditorGUILayout.BeginScrollView(_requestListScroll);
                {
                    var filteredEvents = GetFilteredEvents();

                    foreach (var evt in filteredEvents)
                    {
                        DrawRequestRow(evt);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUILayout.EndVertical();
        }

        private void DrawRequestRow(HttpMonitorEvent evt)
        {
            var isSelected = _selectedEvent == evt;
            var bgColor = isSelected ? new Color(0.3f, 0.5f, 0.8f) : Color.clear;

            var originalBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;

            EditorGUILayout.BeginHorizontal("box", GUILayout.Height(20));
            {
                GUI.backgroundColor = originalBg;

                // Method with color
                var methodColor = GetMethodColor(evt.Method);
                var originalColor = GUI.color;
                GUI.color = methodColor;
                GUILayout.Label(evt.Method, GUILayout.Width(60));
                GUI.color = originalColor;

                // URL
                var urlLabel = evt.Url.Length > 50 ? evt.Url.Substring(0, 47) + "..." : evt.Url;
                GUILayout.Label(urlLabel, GUILayout.ExpandWidth(true));

                // Status
                var statusColor = GetStatusColor(evt.StatusCode);
                GUI.color = statusColor;
                GUILayout.Label(evt.StatusCode.ToString(), GUILayout.Width(50));
                GUI.color = originalColor;

                // Time
                GUILayout.Label($"{evt.ElapsedTime.TotalMilliseconds:F0}ms", GUILayout.Width(60));

                if (Event.current.type == EventType.MouseDown && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    _selectedEvent = evt;
                    _selectedTab = 0;
                    Event.current.Use();
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDetails()
        {
            EditorGUILayout.BeginVertical();
            {
                // Tab bar
                _selectedTab = GUILayout.Toolbar(_selectedTab, _tabNames);

                // Tab content
                _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll);
                {
                    switch (_selectedTab)
                    {
                        case 0: DrawRequestTab(); break;
                        case 1: DrawResponseTab(); break;
                        case 2: DrawTimelineTab(); break;
                        case 3: DrawRawTab(); break;
                    }
                }
                EditorGUILayout.EndScrollView();

                // Actions
                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Copy URL", GUILayout.Width(100)))
                    {
                        EditorGUIUtility.systemCopyBuffer = _selectedEvent.Url;
                    }

                    if (GUILayout.Button("Export", GUILayout.Width(100)))
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
            EditorGUILayout.LabelField("Request Details", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("URL:", _selectedEvent.Url);
            EditorGUILayout.LabelField("Method:", _selectedEvent.Method);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Headers:", EditorStyles.boldLabel);
            foreach (var header in _selectedEvent.RequestHeaders)
            {
                EditorGUILayout.LabelField($"  {header.Key}:", header.Value);
            }

            if (_selectedEvent.RequestBody != null && _selectedEvent.RequestBody.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Body:", EditorStyles.boldLabel);
                var bodyText = _selectedEvent.GetRequestBodyAsString();
                EditorGUILayout.TextArea(bodyText, GUILayout.MinHeight(100));
            }
        }

        private void DrawResponseTab()
        {
            EditorGUILayout.LabelField("Response Details", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Status:", $"{_selectedEvent.StatusCode} {_selectedEvent.StatusText}");
            EditorGUILayout.LabelField("Time:", $"{_selectedEvent.ElapsedTime.TotalMilliseconds:F2}ms");
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Headers:", EditorStyles.boldLabel);
            foreach (var header in _selectedEvent.ResponseHeaders)
            {
                EditorGUILayout.LabelField($"  {header.Key}:", header.Value);
            }

            if (_selectedEvent.ResponseBody != null && _selectedEvent.ResponseBody.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Body:", EditorStyles.boldLabel);
                var bodyText = _selectedEvent.GetResponseBodyAsString();
                EditorGUILayout.TextArea(bodyText, GUILayout.MinHeight(200));
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

            if (_selectedEvent.Timeline != null)
            {
                foreach (var evt in _selectedEvent.Timeline)
                {
                    EditorGUILayout.BeginHorizontal();
                    {
                        EditorGUILayout.LabelField(evt.Name, GUILayout.Width(200));
                        EditorGUILayout.LabelField($"{evt.Timestamp.TotalMilliseconds:F2}ms");
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Total Time:", $"{_selectedEvent.ElapsedTime.TotalMilliseconds:F2}ms");
        }

        private void DrawRawTab()
        {
            EditorGUILayout.LabelField("Raw Data", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            var raw = $"=== REQUEST ===\n{_selectedEvent.Method} {_selectedEvent.Url}\n\n";
            raw += "Headers:\n";
            foreach (var h in _selectedEvent.RequestHeaders)
            {
                raw += $"{h.Key}: {h.Value}\n";
            }
            raw += "\nBody:\n" + _selectedEvent.GetRequestBodyAsString();

            raw += "\n\n=== RESPONSE ===\n";
            raw += $"Status: {_selectedEvent.StatusCode} {_selectedEvent.StatusText}\n\n";
            raw += "Headers:\n";
            foreach (var h in _selectedEvent.ResponseHeaders)
            {
                raw += $"{h.Key}: {h.Value}\n";
            }
            raw += "\nBody:\n" + _selectedEvent.GetResponseBodyAsString();

            EditorGUILayout.TextArea(raw, GUILayout.ExpandHeight(true));
        }

        private List<HttpMonitorEvent> GetFilteredEvents()
        {
            var events = MonitorMiddleware.History.ToList();

            if (!string.IsNullOrWhiteSpace(_filterUrl))
            {
                events = events.Where(e => e.Url.Contains(_filterUrl, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(_filterMethod))
            {
                events = events.Where(e => e.Method.Contains(_filterMethod, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            return events.OrderByDescending(e => e.Timestamp).ToList();
        }

        private Color GetMethodColor(string method)
        {
            return method switch
            {
                "GET" => new Color(0.3f, 0.7f, 0.3f),
                "POST" => new Color(0.3f, 0.5f, 0.9f),
                "PUT" => new Color(0.9f, 0.7f, 0.2f),
                "DELETE" => new Color(0.9f, 0.3f, 0.3f),
                _ => Color.white
            };
        }

        private Color GetStatusColor(int statusCode)
        {
            if (statusCode >= 200 && statusCode < 300) return new Color(0.3f, 0.8f, 0.3f);
            if (statusCode >= 300 && statusCode < 400) return new Color(0.3f, 0.7f, 0.9f);
            if (statusCode >= 400 && statusCode < 500) return new Color(0.9f, 0.7f, 0.2f);
            if (statusCode >= 500) return new Color(0.9f, 0.3f, 0.3f);
            return Color.white;
        }

        private void ExportEvent(HttpMonitorEvent evt)
        {
            var path = EditorUtility.SaveFilePanel("Export HTTP Event", "", $"http_event_{evt.Id}", "json");
            if (!string.IsNullOrEmpty(path))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(evt, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                System.IO.File.WriteAllText(path, json);
                Debug.Log($"Exported to {path}");
            }
        }
    }
}
```

### Task 12.4: Auto-Enable Monitor in Editor

**File:** `Editor/Settings/TurboHttpSettings.cs`

```csharp
using TurboHTTP.Core;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Settings for TurboHTTP in the Unity Editor.
    /// </summary>
    [InitializeOnLoad]
    public static class TurboHttpSettings
    {
        private const string EnableMonitorKey = "TurboHTTP_EnableMonitor";

        public static bool EnableMonitor
        {
            get => EditorPrefs.GetBool(EnableMonitorKey, true);
            set => EditorPrefs.SetBool(EnableMonitorKey, value);
        }

        static TurboHttpSettings()
        {
            // Auto-enable monitor middleware in editor
            if (EnableMonitor)
            {
                EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode && EnableMonitor)
            {
                Debug.Log("[TurboHTTP] Monitor middleware auto-enabled in Editor");
            }
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Preferences/TurboHTTP", SettingsScope.User)
            {
                label = "TurboHTTP",
                guiHandler = (searchContext) =>
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Editor Settings", EditorStyles.boldLabel);

                    EnableMonitor = EditorGUILayout.Toggle("Enable HTTP Monitor", EnableMonitor);
                    EditorGUILayout.HelpBox("When enabled, all HTTP requests will be captured in the HTTP Monitor window.", MessageType.Info);

                    EditorGUILayout.Space();

                    if (GUILayout.Button("Open HTTP Monitor"))
                    {
                        HttpMonitorWindow.ShowWindow();
                    }
                }
            };

            return provider;
        }
    }
}
```

## Validation Criteria

### Success Criteria

- [ ] HTTP Monitor window can be opened from menu
- [ ] All requests are captured and displayed
- [ ] Request details show method, URL, headers, body
- [ ] Response details show status, headers, body
- [ ] Timeline tab shows all events with timestamps
- [ ] Filter by URL works
- [ ] Filter by method works
- [ ] Export functionality works
- [ ] Clear history works
- [ ] Window updates in real-time

### Manual Testing

1. Open HTTP Monitor: Window → TurboHTTP → HTTP Monitor
2. Run any HTTP request in play mode
3. Verify request appears in monitor
4. Click on request to see details
5. Switch between tabs (Request, Response, Timeline, Raw)
6. Test filters
7. Test export
8. Test clear

## Next Steps

Once Phase 12 is complete and validated:

1. Move to [Phase 13: CI/CD & Release](phase-13-release.md)
2. Set up CI/CD pipeline
3. Prepare Asset Store submission
4. M3 milestone near completion

## Notes

- HTTP Monitor is a major differentiator vs competitors
- Real-time updates improve debugging experience dramatically
- Timeline view enables performance optimization
- Export functionality useful for bug reports
- Similar to browser DevTools Network tab
