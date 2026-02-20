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
    public partial class HttpMonitorWindow : EditorWindow
    {
        private const double MinRepaintIntervalSeconds = 0.1d;

        private static readonly HashSet<string> ConfidentialHeaders = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Authorization",
            "Proxy-Authorization",
            "WWW-Authenticate",
            "X-Auth-Token",
            "X-Api-Key",
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
    }
}
