using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Editor;
using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Tests.Editor
{
    [TestFixture]
    public class HttpMonitorEditorTests
    {
        private bool _savedEnableMonitor;
        private int _savedHistoryCapacity;
        private int _savedMaxCaptureSizeMb;
        private int _savedBinaryPreviewKb;
        private bool _savedMaskConfidentialHeaders;

        private bool _savedCaptureEnabled;
        private int _savedMonitorHistoryCapacity;
        private int _savedMonitorMaxCaptureSizeBytes;
        private int _savedMonitorBinaryPreviewBytes;
        private bool _savedWindowDefaultMaskHeaders;

        [SetUp]
        public void SetUp()
        {
            _savedEnableMonitor = TurboHttpSettings.EnableMonitor;
            _savedHistoryCapacity = TurboHttpSettings.HistoryCapacity;
            _savedMaxCaptureSizeMb = TurboHttpSettings.MaxCaptureSizeMb;
            _savedBinaryPreviewKb = TurboHttpSettings.BinaryPreviewKb;
            _savedMaskConfidentialHeaders = TurboHttpSettings.MaskConfidentialHeaders;

            _savedCaptureEnabled = MonitorMiddleware.CaptureEnabled;
            _savedMonitorHistoryCapacity = MonitorMiddleware.HistoryCapacity;
            _savedMonitorMaxCaptureSizeBytes = MonitorMiddleware.MaxCaptureSizeBytes;
            _savedMonitorBinaryPreviewBytes = MonitorMiddleware.BinaryPreviewBytes;
            _savedWindowDefaultMaskHeaders = HttpMonitorWindow.DefaultMaskConfidentialHeaders;

            MonitorMiddleware.ClearHistory();
            CloseAllMonitorWindows();
        }

        [TearDown]
        public void TearDown()
        {
            TurboHttpSettings.EnableMonitor = _savedEnableMonitor;
            TurboHttpSettings.HistoryCapacity = _savedHistoryCapacity;
            TurboHttpSettings.MaxCaptureSizeMb = _savedMaxCaptureSizeMb;
            TurboHttpSettings.BinaryPreviewKb = _savedBinaryPreviewKb;
            TurboHttpSettings.MaskConfidentialHeaders = _savedMaskConfidentialHeaders;
            InvokeApplyMonitorPreferences();

            MonitorMiddleware.CaptureEnabled = _savedCaptureEnabled;
            MonitorMiddleware.HistoryCapacity = _savedMonitorHistoryCapacity;
            MonitorMiddleware.MaxCaptureSizeBytes = _savedMonitorMaxCaptureSizeBytes;
            MonitorMiddleware.BinaryPreviewBytes = _savedMonitorBinaryPreviewBytes;
            HttpMonitorWindow.DefaultMaskConfidentialHeaders = _savedWindowDefaultMaskHeaders;
            MonitorMiddleware.ClearHistory();
            CloseAllMonitorWindows();
        }

        [Test]
        public void ShowWindow_OpensMonitorWindow()
        {
            HttpMonitorWindow.ShowWindow();
            var windows = Resources.FindObjectsOfTypeAll<HttpMonitorWindow>();
            Assert.That(windows, Is.Not.Null);
            Assert.That(windows.Length, Is.GreaterThan(0));
            Assert.That(windows[0].titleContent.text, Is.EqualTo("HTTP Monitor"));
        }

        [Test]
        public void Settings_ApplyAndPersistMonitorValues()
        {
            TurboHttpSettings.EnableMonitor = false;
            TurboHttpSettings.HistoryCapacity = 222;
            TurboHttpSettings.MaxCaptureSizeMb = 7;
            TurboHttpSettings.BinaryPreviewKb = 13;
            TurboHttpSettings.MaskConfidentialHeaders = true;

            Assert.IsFalse(TurboHttpSettings.EnableMonitor);
            Assert.AreEqual(222, TurboHttpSettings.HistoryCapacity);
            Assert.AreEqual(7, TurboHttpSettings.MaxCaptureSizeMb);
            Assert.AreEqual(13, TurboHttpSettings.BinaryPreviewKb);
            Assert.IsTrue(TurboHttpSettings.MaskConfidentialHeaders);

            InvokeApplyMonitorPreferences();

            Assert.IsFalse(MonitorMiddleware.CaptureEnabled);
            Assert.AreEqual(222, MonitorMiddleware.HistoryCapacity);
            Assert.AreEqual(7 * 1024 * 1024, MonitorMiddleware.MaxCaptureSizeBytes);
            Assert.AreEqual(13 * 1024, MonitorMiddleware.BinaryPreviewBytes);
            Assert.IsTrue(HttpMonitorWindow.DefaultMaskConfidentialHeaders);
        }

        [Test]
        public void SettingsProvider_CreatesExpectedPreferencesEntry()
        {
            var provider = TurboHttpSettings.CreateSettingsProvider();
            Assert.IsNotNull(provider);
            Assert.AreEqual("Preferences/TurboHTTP", provider.settingsPath);
            Assert.AreEqual("TurboHTTP", provider.label);
            Assert.IsNotNull(provider.guiHandler);
        }

        [Test]
        public void ReplayBuilder_BuildsReplayRequestFromCapturedEvent()
        {
            MonitorMiddleware.ClearHistory();
            MonitorMiddleware.MaxCaptureSizeBytes = 4;
            MonitorMiddleware.BinaryPreviewBytes = 4;

            var requestHeaders = new HttpHeaders();
            requestHeaders.Set("Host", "api.example.com");
            requestHeaders.Set("Authorization", "Bearer token-123");
            requestHeaders.Set("Content-Type", "text/plain");

            var request = new UHttpRequest(
                HttpMethod.POST,
                new Uri("https://api.example.com/replay"),
                requestHeaders,
                Encoding.UTF8.GetBytes("abcdefghi"));

            var pipeline = new HttpPipeline(
                new IHttpMiddleware[] { new MonitorMiddleware() },
                new SuccessfulStubTransport());

            pipeline.ExecuteAsync(request, new RequestContext(request)).GetAwaiter().GetResult();

            var snapshot = new List<HttpMonitorEvent>();
            MonitorMiddleware.GetHistorySnapshot(snapshot);
            Assert.AreEqual(1, snapshot.Count);

            var success = InvokeTryBuildReplayRequest(
                snapshot[0],
                out var replayRequest,
                out var warning,
                out var error);

            Assert.IsTrue(success);
            Assert.IsNotNull(replayRequest);
            Assert.AreEqual(HttpMethod.POST, replayRequest.Method);
            Assert.AreEqual("https://api.example.com/replay", replayRequest.Uri.ToString());
            Assert.AreEqual("Bearer token-123", replayRequest.Headers.Get("Authorization"));
            Assert.IsFalse(replayRequest.Headers.Contains("Host"));
            Assert.IsNotNull(replayRequest.Body);
            Assert.AreEqual(4, replayRequest.Body.Length);
            Assert.That(warning, Does.Contain("truncated"));
            Assert.That(warning, Does.Contain("Transport-managed headers"));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void ReplayBuilder_NullEvent_ReturnsError()
        {
            var success = InvokeTryBuildReplayRequest(
                null,
                out var replayRequest,
                out var warning,
                out var error);

            Assert.IsFalse(success);
            Assert.IsNull(replayRequest);
            Assert.That(warning, Is.Empty);
            Assert.That(error, Is.EqualTo("No monitor event is selected."));
        }

        private static bool InvokeTryBuildReplayRequest(
            HttpMonitorEvent evt,
            out UHttpRequest request,
            out string warning,
            out string error)
        {
            var method = typeof(HttpMonitorWindow).GetMethod(
                "TryBuildReplayRequest",
                BindingFlags.Static | BindingFlags.NonPublic);

            if (method == null)
            {
                throw new MissingMethodException(
                    nameof(HttpMonitorWindow),
                    "TryBuildReplayRequest");
            }

            object[] args = { evt, null, null, null };
            var result = method.Invoke(null, args);

            request = (UHttpRequest)args[1];
            warning = args[2] as string ?? string.Empty;
            error = args[3] as string ?? string.Empty;
            return result is bool value && value;
        }

        private static void InvokeApplyMonitorPreferences()
        {
            var method = typeof(TurboHttpSettings).GetMethod(
                "ApplyMonitorPreferences",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(
                    nameof(TurboHttpSettings),
                    "ApplyMonitorPreferences");
            }

            method.Invoke(null, null);
        }

        private static void CloseAllMonitorWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<HttpMonitorWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null)
                {
                    windows[i].Close();
                }
            }
        }

        private sealed class SuccessfulStubTransport : IHttpTransport
        {
            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return new ValueTask<UHttpResponse>(new UHttpResponse(
                    HttpStatusCode.OK,
                    new HttpHeaders(),
                    Array.Empty<byte>(),
                    TimeSpan.Zero,
                    request));
            }

            public void Dispose()
            {
            }
        }
    }
}
