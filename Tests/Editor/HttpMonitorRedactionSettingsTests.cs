using System;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Editor;
using TurboHTTP.Observability;
using UnityEngine;

namespace TurboHTTP.Tests.Editor
{
    /// <summary>
    /// #92: Monitor preferences for capture-time redaction, capture-while-closed and the byte budget,
    /// and the window's viewer registration (capture only while it is open).
    /// </summary>
    [TestFixture]
    public class HttpMonitorRedactionSettingsTests
    {
        private bool _savedRedact;
        private bool _savedCaptureWhileClosed;
        private int _savedBudgetMb;

        [SetUp]
        public void SetUp()
        {
            _savedRedact = TurboHttpSettings.RedactCapturedSecrets;
            _savedCaptureWhileClosed = TurboHttpSettings.CaptureWhileMonitorClosed;
            _savedBudgetMb = TurboHttpSettings.MaxTotalCaptureMb;
            CloseAllMonitorWindows();
        }

        [TearDown]
        public void TearDown()
        {
            TurboHttpSettings.RedactCapturedSecrets = _savedRedact;
            TurboHttpSettings.CaptureWhileMonitorClosed = _savedCaptureWhileClosed;
            TurboHttpSettings.MaxTotalCaptureMb = _savedBudgetMb;
            InvokeApplyMonitorPreferences();
            CloseAllMonitorWindows();
        }

        [Test]
        public void ApplyPreferences_SetsRedactionCaptureGateAndBudget()
        {
            TurboHttpSettings.RedactCapturedSecrets = false;
            TurboHttpSettings.CaptureWhileMonitorClosed = true;
            TurboHttpSettings.MaxTotalCaptureMb = 7;
            InvokeApplyMonitorPreferences();

            Assert.AreSame(RedactionPolicy.None, MonitorInterceptor.RedactionPolicy);
            Assert.IsTrue(MonitorInterceptor.CaptureWithoutViewer);
            Assert.AreEqual(7L * 1024 * 1024, MonitorInterceptor.MaxTotalCaptureBytes);

            TurboHttpSettings.RedactCapturedSecrets = true;
            TurboHttpSettings.CaptureWhileMonitorClosed = false;
            InvokeApplyMonitorPreferences();

            Assert.AreSame(RedactionPolicy.Default, MonitorInterceptor.RedactionPolicy);
            Assert.IsFalse(MonitorInterceptor.CaptureWithoutViewer);
        }

        [Test]
        public void MaxTotalCaptureMb_IsClamped()
        {
            TurboHttpSettings.MaxTotalCaptureMb = 0;
            Assert.AreEqual(1, TurboHttpSettings.MaxTotalCaptureMb);
            TurboHttpSettings.MaxTotalCaptureMb = 100000;
            Assert.AreEqual(1024, TurboHttpSettings.MaxTotalCaptureMb);
        }

        [Test]
        public void MonitorWindow_AttachesViewerOnlyWhileOpen()
        {
            var before = MonitorInterceptor.ViewerCount;

            HttpMonitorWindow.ShowWindow();
            Assert.AreEqual(before + 1, MonitorInterceptor.ViewerCount);

            CloseAllMonitorWindows();
            Assert.AreEqual(before, MonitorInterceptor.ViewerCount);
        }

        private static void CloseAllMonitorWindows()
        {
            var windows = Resources.FindObjectsOfTypeAll<HttpMonitorWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i] != null)
                    windows[i].Close();
            }
        }

        private static void InvokeApplyMonitorPreferences()
        {
            var method = typeof(TurboHttpSettings).GetMethod(
                "ApplyMonitorPreferences",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException(nameof(TurboHttpSettings), "ApplyMonitorPreferences");

            method.Invoke(null, null);
        }
    }
}
