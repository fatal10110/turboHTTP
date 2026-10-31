using TurboHTTP.Observability;
using UnityEditor;
using UnityEngine;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Unity Editor preferences for TurboHTTP monitor tooling.
    /// </summary>
    [InitializeOnLoad]
    public static class TurboHttpSettings
    {
        private const string EnableMonitorKey = "TurboHTTP_EnableMonitor";
        private const string HistoryCapacityKey = "TurboHTTP_HistoryCapacity";
        private const string MaxCaptureSizeMbKey = "TurboHTTP_MaxCaptureSizeMb";
        private const string BinaryPreviewKbKey = "TurboHTTP_BinaryPreviewKb";
        private const string MaskConfidentialHeadersKey = "TurboHTTP_MaskConfidentialHeaders";
        private const string RedactCapturedSecretsKey = "TurboHTTP_RedactCapturedSecrets";
        private const string CaptureWhileClosedKey = "TurboHTTP_CaptureWhileMonitorClosed";
        private const string MaxTotalCaptureMbKey = "TurboHTTP_MaxTotalCaptureMb";

        private const int DefaultHistoryCapacity = 1000;
        private const int DefaultMaxCaptureSizeMb = 5;
        private const int DefaultBinaryPreviewKb = 64;
        private const bool DefaultMaskConfidentialHeaders = true;
        private const bool DefaultRedactCapturedSecrets = true;
        private const bool DefaultCaptureWhileClosed = false;
        private const int DefaultMaxTotalCaptureMb = 32;
        private const int MinMaxTotalCaptureMb = 1;
        private const int MaxMaxTotalCaptureMb = 1024;

        private const int MinHistoryCapacity = 10;
        private const int MaxHistoryCapacity = 10000;
        private const int MinMaxCaptureSizeMb = 1;
        private const int MaxMaxCaptureSizeMb = 50;
        private const int MinBinaryPreviewKb = 1;
        private const int MaxBinaryPreviewKb = 1024;

        public static bool EnableMonitor
        {
            get => EditorPrefs.GetBool(EnableMonitorKey, true);
            set => EditorPrefs.SetBool(EnableMonitorKey, value);
        }

        public static int HistoryCapacity
        {
            get => Mathf.Clamp(
                EditorPrefs.GetInt(HistoryCapacityKey, DefaultHistoryCapacity),
                MinHistoryCapacity,
                MaxHistoryCapacity);
            set => EditorPrefs.SetInt(
                HistoryCapacityKey,
                Mathf.Clamp(value, MinHistoryCapacity, MaxHistoryCapacity));
        }

        public static int MaxCaptureSizeMb
        {
            get => Mathf.Clamp(
                EditorPrefs.GetInt(MaxCaptureSizeMbKey, DefaultMaxCaptureSizeMb),
                MinMaxCaptureSizeMb,
                MaxMaxCaptureSizeMb);
            set => EditorPrefs.SetInt(
                MaxCaptureSizeMbKey,
                Mathf.Clamp(value, MinMaxCaptureSizeMb, MaxMaxCaptureSizeMb));
        }

        public static int BinaryPreviewKb
        {
            get => Mathf.Clamp(
                EditorPrefs.GetInt(BinaryPreviewKbKey, DefaultBinaryPreviewKb),
                MinBinaryPreviewKb,
                MaxBinaryPreviewKb);
            set => EditorPrefs.SetInt(
                BinaryPreviewKbKey,
                Mathf.Clamp(value, MinBinaryPreviewKb, MaxBinaryPreviewKb));
        }

        public static bool MaskConfidentialHeaders
        {
            get => EditorPrefs.GetBool(MaskConfidentialHeadersKey, DefaultMaskConfidentialHeaders);
            set => EditorPrefs.SetBool(MaskConfidentialHeadersKey, value);
        }

        /// <summary>
        /// Redact secrets (sensitive header values, URL query values and user-info) when the Monitor
        /// captures a request. Default <c>true</c>; turning it off stores raw credentials in memory.
        /// </summary>
        public static bool RedactCapturedSecrets
        {
            get => EditorPrefs.GetBool(RedactCapturedSecretsKey, DefaultRedactCapturedSecrets);
            set => EditorPrefs.SetBool(RedactCapturedSecretsKey, value);
        }

        /// <summary>
        /// Capture requests while no HTTP Monitor window is open. Default <c>false</c>.
        /// </summary>
        public static bool CaptureWhileMonitorClosed
        {
            get => EditorPrefs.GetBool(CaptureWhileClosedKey, DefaultCaptureWhileClosed);
            set => EditorPrefs.SetBool(CaptureWhileClosedKey, value);
        }

        /// <summary>
        /// Total bytes the capture history may hold, in MB (oldest entries are evicted). Default 32.
        /// </summary>
        public static int MaxTotalCaptureMb
        {
            get => Mathf.Clamp(
                EditorPrefs.GetInt(MaxTotalCaptureMbKey, DefaultMaxTotalCaptureMb),
                MinMaxTotalCaptureMb,
                MaxMaxTotalCaptureMb);
            set => EditorPrefs.SetInt(
                MaxTotalCaptureMbKey,
                Mathf.Clamp(value, MinMaxTotalCaptureMb, MaxMaxTotalCaptureMb));
        }

        static TurboHttpSettings()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            ApplyMonitorPreferences();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode
                && state != PlayModeStateChange.EnteredEditMode)
            {
                return;
            }

            ApplyMonitorPreferences();
        }

        private static void ApplyMonitorPreferences()
        {
            MonitorInterceptor.CaptureEnabled = EnableMonitor;
            MonitorInterceptor.HistoryCapacity = HistoryCapacity;
            MonitorInterceptor.MaxCaptureSizeBytes = MaxCaptureSizeMb * 1024 * 1024;
            MonitorInterceptor.BinaryPreviewBytes = BinaryPreviewKb * 1024;
            MonitorInterceptor.MaxTotalCaptureBytes = MaxTotalCaptureMb * 1024L * 1024L;
            MonitorInterceptor.CaptureWithoutViewer = CaptureWhileMonitorClosed;
            MonitorInterceptor.RedactionPolicy = RedactCapturedSecrets
                ? RedactionPolicy.Default
                : RedactionPolicy.None;
            HttpMonitorWindow.DefaultMaskConfidentialHeaders = MaskConfidentialHeaders;
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            var provider = new SettingsProvider("Preferences/TurboHTTP", SettingsScope.User)
            {
                label = "TurboHTTP",
                guiHandler = _ =>
                {
                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("HTTP Monitor", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();

                    var enableMonitor = EditorGUILayout.Toggle("Enable HTTP Monitor", EnableMonitor);
                    var historyCapacity = EditorGUILayout.IntField("History Capacity", HistoryCapacity);
                    var maxCaptureMb = EditorGUILayout.IntField("Max Capture Size (MB)", MaxCaptureSizeMb);
                    var binaryPreviewKb = EditorGUILayout.IntField("Binary Preview (KB)", BinaryPreviewKb);
                    var maxTotalCaptureMb = EditorGUILayout.IntField("Total Capture Budget (MB)", MaxTotalCaptureMb);
                    var redactSecrets = EditorGUILayout.Toggle("Redact Captured Secrets", RedactCapturedSecrets);
                    var captureWhileClosed = EditorGUILayout.Toggle(
                        "Capture While Monitor Closed",
                        CaptureWhileMonitorClosed);
                    var maskHeaders = EditorGUILayout.Toggle(
                        "Mask Confidential Headers (Default)",
                        MaskConfidentialHeaders);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EnableMonitor = enableMonitor;
                        HistoryCapacity = historyCapacity;
                        MaxCaptureSizeMb = maxCaptureMb;
                        BinaryPreviewKb = binaryPreviewKb;
                        MaxTotalCaptureMb = maxTotalCaptureMb;
                        RedactCapturedSecrets = redactSecrets;
                        CaptureWhileMonitorClosed = captureWhileClosed;
                        MaskConfidentialHeaders = maskHeaders;
                        ApplyMonitorPreferences();
                    }

                    EditorGUILayout.HelpBox(
                        "UI settings are clamped to safe ranges: history 10-10000 entries, " +
                        "max capture 1-50 MB, binary preview 1-1024 KB, total budget 1-1024 MB. " +
                        "Capture runs only while the HTTP Monitor window is open unless " +
                        "\"Capture While Monitor Closed\" is set.",
                        MessageType.Info);

                    EditorGUILayout.Space();
                    if (GUILayout.Button("Open HTTP Monitor"))
                    {
                        HttpMonitorWindow.ShowWindow();
                    }
                },
                keywords = new System.Collections.Generic.HashSet<string>(new[]
                {
                    "TurboHTTP",
                    "HTTP",
                    "Monitor",
                    "Capture",
                    "History",
                    "Binary",
                    "Headers"
                })
            };

            return provider;
        }
    }
}
