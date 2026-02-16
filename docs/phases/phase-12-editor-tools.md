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
using System.Text;
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
        public IReadOnlyDictionary<string, string> RequestHeaders { get; set; }
        public byte[] RequestBody { get; set; }
        public int OriginalRequestBodySize { get; set; }
        public bool IsRequestBodyTruncated { get; set; }
        public bool IsRequestBodyBinary { get; set; }

        // Response
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public IReadOnlyDictionary<string, string> ResponseHeaders { get; set; }
        public byte[] ResponseBody { get; set; }
        public int OriginalResponseBodySize { get; set; }
        public bool IsResponseBodyTruncated { get; set; }
        public bool IsResponseBodyBinary { get; set; }

        // Timing
        public TimeSpan ElapsedTime { get; set; }
        public IReadOnlyList<TimelineEvent> Timeline { get; set; }

        // Error
        public string Error { get; set; }

        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
        public bool IsError => !string.IsNullOrEmpty(Error);
        private static readonly UTF8Encoding Utf8NoThrow = new UTF8Encoding(false, false);

        public string GetRequestBodyAsString()
        {
            return GetBodyAsString(
                RequestBody,
                RequestHeaders,
                IsRequestBodyBinary,
                IsRequestBodyTruncated,
                OriginalRequestBodySize);
        }

        public string GetResponseBodyAsString()
        {
            return GetBodyAsString(
                ResponseBody,
                ResponseHeaders,
                IsResponseBodyBinary,
                IsResponseBodyTruncated,
                OriginalResponseBodySize);
        }

        private static string GetBodyAsString(
            byte[] body,
            IReadOnlyDictionary<string, string> headers,
            bool isBinary,
            bool isTruncated,
            int originalSize)
        {
            if (body == null || body.Length == 0)
            {
                return string.Empty;
            }

            var totalSize = originalSize > 0 ? originalSize : body.Length;
            if (isBinary || HasBinaryContentType(headers) || ContainsNullByte(body))
            {
                return $"<Binary Data: {totalSize} bytes{(isTruncated ? ", preview only" : string.Empty)}>";
            }

            var text = Utf8NoThrow.GetString(body);
            if (isTruncated)
            {
                text += $"\n\n<Truncated: showing {body.Length}/{totalSize} bytes>";
            }

            return text;
        }

        private static bool HasBinaryContentType(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null
                || !headers.TryGetValue("Content-Type", out var contentType)
                || string.IsNullOrEmpty(contentType))
            {
                return false;
            }

            return contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("application/vnd.unity", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsNullByte(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    return true;
                }
            }

            return false;
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
        private static readonly object _historyLock = new object();
        private static readonly List<HttpMonitorEvent> _history = new List<HttpMonitorEvent>(1000);

        private const int DefaultHistoryCapacity = 1000;
        private const int DefaultMaxCaptureSizeBytes = 5 * 1024 * 1024; // 5 MB
        private const int DefaultBinaryPreviewBytes = 64 * 1024; // 64 KB
        private static readonly TimeSpan CaptureErrorLogCooldown = TimeSpan.FromSeconds(30);

        private static int _historyCapacity = DefaultHistoryCapacity;
        private static int _maxCaptureSizeBytes = DefaultMaxCaptureSizeBytes;
        private static int _binaryPreviewBytes = DefaultBinaryPreviewBytes;
        private static DateTime _nextCaptureErrorLogUtc = DateTime.MinValue;

        // Optional hook; default is pass-through (no masking/redaction).
        public static Func<string, string, string> HeaderValueTransform { get; set; } = (key, value) => value;
        public static Action<string> DiagnosticLogger { get; set; }

        public static int HistoryCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _history.Count;
                }
            }
        }

        public static int HistoryCapacity
        {
            get => _historyCapacity;
            set => _historyCapacity = Math.Max(10, value);
        }

        public static int MaxCaptureSizeBytes
        {
            get => _maxCaptureSizeBytes;
            set => _maxCaptureSizeBytes = Math.Max(1024, value);
        }

        public static int BinaryPreviewBytes
        {
            get => _binaryPreviewBytes;
            set => _binaryPreviewBytes = Math.Max(256, value);
        }

        public static void GetHistorySnapshot(List<HttpMonitorEvent> buffer)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }

            lock (_historyLock)
            {
                buffer.Clear();
                buffer.AddRange(_history);
            }
        }

        public static void ClearHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
            }

            // Null payload indicates structural update (for example clear).
            PublishEvent(null);
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            HttpMonitorEvent monitorEvent = null;
            UHttpResponse response = null;
            Exception exception = null;

            try
            {
                response = await next(request, context, cancellationToken).ConfigureAwait(false);
                return response;
            }
            catch (Exception ex)
            {
                exception = ex;
                throw;
            }
            finally
            {
                try
                {
                    monitorEvent = BuildMonitorEvent(request, response, context, exception);
                    StoreEvent(monitorEvent);
                    PublishEvent(monitorEvent);
                }
                catch (Exception captureEx)
                {
                    LogCaptureFailure(captureEx);
                }
            }
        }

        private static HttpMonitorEvent BuildMonitorEvent(
            UHttpRequest request,
            UHttpResponse response,
            RequestContext context,
            Exception exception)
        {
            var requestHeaders = CopyHeaders(request.Headers);
            var requestSnapshot = CreateBodySnapshot(request.Body, requestHeaders);

            var responseHeaders = response != null
                ? CopyHeaders(response.Headers)
                : new Dictionary<string, string>();

            var responseSnapshot = response != null
                ? CreateBodySnapshot(response.Body, responseHeaders)
                : (Body: (byte[])null, OriginalSize: 0, IsTruncated: false, IsBinary: false);

            return new HttpMonitorEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = DateTime.UtcNow,
                Method = request.Method.ToString(),
                Url = request.Uri?.ToString() ?? string.Empty,
                RequestHeaders = requestHeaders,
                RequestBody = requestSnapshot.Body,
                OriginalRequestBodySize = requestSnapshot.OriginalSize,
                IsRequestBodyTruncated = requestSnapshot.IsTruncated,
                IsRequestBodyBinary = requestSnapshot.IsBinary,
                StatusCode = response != null ? (int)response.StatusCode : 0,
                StatusText = response?.StatusCode.ToString(),
                ResponseHeaders = responseHeaders,
                ResponseBody = responseSnapshot.Body,
                OriginalResponseBodySize = responseSnapshot.OriginalSize,
                IsResponseBodyTruncated = responseSnapshot.IsTruncated,
                IsResponseBodyBinary = responseSnapshot.IsBinary,
                Error = response?.Error?.ToString() ?? exception?.Message,
                ElapsedTime = context?.Elapsed ?? TimeSpan.Zero,
                Timeline = context?.Timeline?.ToList() ?? new List<TimelineEvent>()
            };
        }

        private static Dictionary<string, string> CopyHeaders(IEnumerable<KeyValuePair<string, string>> headers)
        {
            var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers == null)
            {
                return copy;
            }

            foreach (var header in headers)
            {
                var transformed = HeaderValueTransform?.Invoke(header.Key, header.Value) ?? header.Value;
                copy[header.Key] = transformed;
            }

            return copy;
        }

        private static (byte[] Body, int OriginalSize, bool IsTruncated, bool IsBinary) CreateBodySnapshot(
            byte[] body,
            IReadOnlyDictionary<string, string> headers)
        {
            if (body == null || body.Length == 0)
            {
                return (null, 0, false, false);
            }

            var originalSize = body.Length;
            var isBinary = IsBinaryPayload(body, headers);
            var limit = isBinary ? BinaryPreviewBytes : MaxCaptureSizeBytes;
            var captureSize = Math.Min(originalSize, Math.Max(0, limit));
            var isTruncated = originalSize > captureSize;

            if (captureSize <= 0)
            {
                return (Array.Empty<byte>(), originalSize, true, isBinary);
            }

            var snapshot = new byte[captureSize];
            Buffer.BlockCopy(body, 0, snapshot, 0, captureSize);
            return (snapshot, originalSize, isTruncated, isBinary);
        }

        private static bool IsBinaryPayload(byte[] body, IReadOnlyDictionary<string, string> headers)
        {
            if (headers != null
                && headers.TryGetValue("Content-Type", out var contentType)
                && !string.IsNullOrEmpty(contentType))
            {
                if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    || contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    || contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase)
                    || contentType.Contains("application/vnd.unity", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var checkLength = Math.Min(body.Length, 512);
            for (var i = 0; i < checkLength; i++)
            {
                if (body[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static void StoreEvent(HttpMonitorEvent monitorEvent)
        {
            lock (_historyLock)
            {
                _history.Add(monitorEvent);
                while (_history.Count > HistoryCapacity)
                {
                    _history.RemoveAt(0);
                }
            }
        }

        private static void PublishEvent(HttpMonitorEvent monitorEvent)
        {
            var handlers = OnRequestCaptured;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<HttpMonitorEvent> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(monitorEvent);
                }
                catch (Exception ex)
                {
                    LogCaptureFailure(ex);
                }
            }
        }

        private static void LogCaptureFailure(Exception ex)
        {
            var shouldLog = false;
            lock (_historyLock)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextCaptureErrorLogUtc)
                {
                    _nextCaptureErrorLogUtc = now + CaptureErrorLogCooldown;
                    shouldLog = true;
                }
            }

            if (shouldLog)
            {
                DiagnosticLogger?.Invoke($"[TurboHTTP][Monitor] Capture failure: {ex.Message}");
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
using System.Text;
using System.Threading;
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
        public static bool DefaultMaskConfidentialHeaders { get; set; }

        private static readonly HashSet<string> ConfidentialHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "X-Auth-Token",
            "X-API-Key",
            "Cookie",
            "Set-Cookie"
        };

        private readonly List<HttpMonitorEvent> _historyCache = new List<HttpMonitorEvent>(1024);
        private readonly List<HttpMonitorEvent> _filteredCache = new List<HttpMonitorEvent>(1024);
        private Vector2 _requestListScroll;
        private Vector2 _detailsScroll;
        private HttpMonitorEvent _selectedEvent;
        private string _filterUrl = string.Empty;
        private string _filterMethod = string.Empty;
        private bool _maskConfidentialHeaders;
        private bool _historyDirty = true;
        private int _selectedTab;
        private int _pendingUiRefresh;
        private readonly string[] _tabNames = { "Request", "Response", "Timeline", "Raw" };

        [MenuItem("Window/TurboHTTP/HTTP Monitor")]
        public static void ShowWindow()
        {
            var window = GetWindow<HttpMonitorWindow>("HTTP Monitor");
            window.minSize = new Vector2(800, 400);
        }

        private void OnEnable()
        {
            _maskConfidentialHeaders = DefaultMaskConfidentialHeaders;
            MonitorMiddleware.OnRequestCaptured += OnRequestCaptured;
            RefreshHistoryCache();
        }

        private void OnDisable()
        {
            MonitorMiddleware.OnRequestCaptured -= OnRequestCaptured;
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
            Interlocked.Exchange(ref _pendingUiRefresh, 0);
            _historyDirty = true;
            Repaint();
        }

        private void OnGUI()
        {
            RefreshHistoryCacheIfDirty();
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

        private void RefreshHistoryCacheIfDirty()
        {
            if (!_historyDirty)
            {
                return;
            }

            RefreshHistoryCache();
            _historyDirty = false;

            if (_selectedEvent != null && !_historyCache.Contains(_selectedEvent))
            {
                _selectedEvent = null;
            }
        }

        private void RefreshHistoryCache()
        {
            MonitorMiddleware.GetHistorySnapshot(_historyCache);
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            {
                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(60)))
                {
                    MonitorMiddleware.ClearHistory();
                    _selectedEvent = null;
                    _historyDirty = true;
                }

                GUILayout.Space(8);
                _maskConfidentialHeaders = GUILayout.Toggle(
                    _maskConfidentialHeaders,
                    "Mask Confidential Headers",
                    EditorStyles.toolbarButton,
                    GUILayout.Width(170));

                GUILayout.FlexibleSpace();

                GUILayout.Label("Filter:", GUILayout.Width(40));
                _filterUrl = GUILayout.TextField(_filterUrl, EditorStyles.toolbarSearchField, GUILayout.Width(200));

                GUILayout.Label("Method:", GUILayout.Width(50));
                _filterMethod = GUILayout.TextField(_filterMethod, EditorStyles.toolbarSearchField, GUILayout.Width(60));

                GUILayout.Label($"Total: {_historyCache.Count}", GUILayout.Width(90));
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
                    BuildFilteredCache();
                    for (var i = 0; i < _filteredCache.Count; i++)
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
                var url = evt.Url ?? string.Empty;
                var urlLabel = url.Length > 50 ? url.Substring(0, 47) + "..." : url;
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

            EditorGUILayout.LabelField("URL:", _selectedEvent.Url ?? string.Empty);
            EditorGUILayout.LabelField("Method:", _selectedEvent.Method ?? string.Empty);
            DrawBodyMeta(_selectedEvent.OriginalRequestBodySize, _selectedEvent.IsRequestBodyTruncated, _selectedEvent.IsRequestBodyBinary);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Headers:", EditorStyles.boldLabel);
            DrawHeaders(_selectedEvent.RequestHeaders);

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
            DrawBodyMeta(_selectedEvent.OriginalResponseBodySize, _selectedEvent.IsResponseBodyTruncated, _selectedEvent.IsResponseBodyBinary);
            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Headers:", EditorStyles.boldLabel);
            DrawHeaders(_selectedEvent.ResponseHeaders);

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
            var raw = new StringBuilder(4096);
            raw.AppendLine("=== REQUEST ===");
            raw.AppendLine($"{_selectedEvent.Method} {_selectedEvent.Url}");
            raw.AppendLine();
            raw.AppendLine("Headers:");
            AppendHeaders(raw, _selectedEvent.RequestHeaders);
            raw.AppendLine();
            raw.AppendLine("Body:");
            raw.AppendLine(_selectedEvent.GetRequestBodyAsString());
            raw.AppendLine();
            raw.AppendLine("=== RESPONSE ===");
            raw.AppendLine($"Status: {_selectedEvent.StatusCode} {_selectedEvent.StatusText}");
            raw.AppendLine();
            raw.AppendLine("Headers:");
            AppendHeaders(raw, _selectedEvent.ResponseHeaders);
            raw.AppendLine();
            raw.AppendLine("Body:");
            raw.AppendLine(_selectedEvent.GetResponseBodyAsString());

            EditorGUILayout.TextArea(raw.ToString(), GUILayout.ExpandHeight(true));
        }

        private void BuildFilteredCache()
        {
            _filteredCache.Clear();
            for (var i = _historyCache.Count - 1; i >= 0; i--)
            {
                var evt = _historyCache[i];
                if (!MatchesFilter(evt))
                {
                    continue;
                }

                _filteredCache.Add(evt);
            }
        }

        private bool MatchesFilter(HttpMonitorEvent evt)
        {
            if (evt == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterUrl)
                && (evt.Url == null || evt.Url.IndexOf(_filterUrl, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(_filterMethod)
                && (evt.Method == null || evt.Method.IndexOf(_filterMethod, StringComparison.OrdinalIgnoreCase) < 0))
            {
                return false;
            }

            return true;
        }

        private void DrawBodyMeta(int originalSize, bool truncated, bool binary)
        {
            var label = $"{originalSize} bytes";
            if (binary)
            {
                label += " (binary)";
            }

            if (truncated)
            {
                label += " - truncated/preview";
            }

            EditorGUILayout.LabelField("Captured Body:", label);
        }

        private void DrawHeaders(IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                EditorGUILayout.LabelField("  <none>");
                return;
            }

            foreach (var header in headers)
            {
                EditorGUILayout.LabelField($"  {header.Key}:", FormatHeaderValue(header.Key, header.Value));
            }
        }

        private void AppendHeaders(StringBuilder builder, IReadOnlyDictionary<string, string> headers)
        {
            if (headers == null || headers.Count == 0)
            {
                return;
            }

            foreach (var header in headers)
            {
                builder.Append(header.Key);
                builder.Append(": ");
                builder.AppendLine(FormatHeaderValue(header.Key, header.Value));
            }
        }

        private string FormatHeaderValue(string key, string value)
        {
            if (_maskConfidentialHeaders && key != null && ConfidentialHeaders.Contains(key))
            {
                return "********";
            }

            return value ?? string.Empty;
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
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(evt, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            System.IO.File.WriteAllText(path, json);
            Debug.Log($"Exported to {path}");
        }
    }
}
```

### Task 12.4: Auto-Enable Monitor in Editor

**File:** `Editor/Settings/TurboHttpSettings.cs`

```csharp
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
        private const string MaxCaptureSizeMbKey = "TurboHTTP_MaxCaptureSizeMb";
        private const string MaskConfidentialHeadersKey = "TurboHTTP_MaskConfidentialHeaders";
        private const int DefaultMaxCaptureSizeMb = 5;
        private const bool DefaultMaskConfidentialHeaders = false;

        public static bool EnableMonitor
        {
            get => EditorPrefs.GetBool(EnableMonitorKey, true);
            set => EditorPrefs.SetBool(EnableMonitorKey, value);
        }

        public static int MaxCaptureSizeMb
        {
            get => Mathf.Clamp(EditorPrefs.GetInt(MaxCaptureSizeMbKey, DefaultMaxCaptureSizeMb), 1, 50);
            set => EditorPrefs.SetInt(MaxCaptureSizeMbKey, Mathf.Clamp(value, 1, 50));
        }

        public static bool MaskConfidentialHeaders
        {
            get => EditorPrefs.GetBool(MaskConfidentialHeadersKey, DefaultMaskConfidentialHeaders);
            set => EditorPrefs.SetBool(MaskConfidentialHeadersKey, value);
        }

        static TurboHttpSettings()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ApplyMonitorPreferences();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode)
            {
                return;
            }

            ApplyMonitorPreferences();
            if (EnableMonitor)
            {
                Debug.Log("[TurboHTTP] Monitor middleware configured in Editor play mode.");
            }
        }

        private static void ApplyMonitorPreferences()
        {
            MonitorMiddleware.MaxCaptureSizeBytes = MaxCaptureSizeMb * 1024 * 1024;
            HttpMonitorWindow.DefaultMaskConfidentialHeaders = MaskConfidentialHeaders;
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
                    MaxCaptureSizeMb = EditorGUILayout.IntField("Max Capture Size (MB)", MaxCaptureSizeMb);
                    MaskConfidentialHeaders = EditorGUILayout.Toggle("Mask Confidential Headers", MaskConfidentialHeaders);

                    EditorGUILayout.HelpBox(
                        "Text payloads are captured up to Max Capture Size (default 5 MB). Binary payloads store preview/metadata by default.",
                        MessageType.Info);

                    ApplyMonitorPreferences();

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
- [ ] Large text payloads are preserved up to configured `MaxCaptureSize` (default 5 MB)
- [ ] Large binary payloads show preview/metadata instead of full body allocation
- [ ] Header masking toggle works and defaults to OFF
- [ ] Export functionality works
- [ ] Clear history works
- [ ] Window updates in real-time without per-`OnGUI` history list allocations
- [ ] Window repaint scheduling remains main-thread safe

### Manual Testing

1. Open HTTP Monitor: Window → TurboHTTP → HTTP Monitor
2. Run any HTTP request in play mode
3. Verify request appears in monitor
4. Click on request to see details
5. Switch between tabs (Request, Response, Timeline, Raw)
6. Test filters
7. Verify binary payload request/response renders placeholder/preview text
8. Toggle "Mask Confidential Headers" and verify expected header masking behavior
9. Test export
10. Test clear

## Next Steps

Once Phase 12 is complete and validated:

1. Move to [Phase 14: Post-v1.0 Roadmap](phase-14-future.md)
2. Continue through Phases 15 and 16
3. Finish with [Phase 17: CI/CD & Release](phase-17-release.md)
4. M3 milestone complete; proceed toward M4/M5

## Notes

- HTTP Monitor is a major differentiator vs competitors
- Real-time updates improve debugging experience dramatically
- Timeline view enables performance optimization
- Export functionality useful for bug reports
- Similar to browser DevTools Network tab
