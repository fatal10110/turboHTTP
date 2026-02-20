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

        private const int DefaultHistoryCapacity = 1000;
        private const int DefaultMaxCaptureSizeMb = 5;
        private const int DefaultBinaryPreviewKb = 64;
        private const bool DefaultMaskConfidentialHeaders = false;

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
            MonitorMiddleware.CaptureEnabled = EnableMonitor;
            MonitorMiddleware.HistoryCapacity = HistoryCapacity;
            MonitorMiddleware.MaxCaptureSizeBytes = MaxCaptureSizeMb * 1024 * 1024;
            MonitorMiddleware.BinaryPreviewBytes = BinaryPreviewKb * 1024;
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
                    var maskHeaders = EditorGUILayout.Toggle(
                        "Mask Confidential Headers (Default)",
                        MaskConfidentialHeaders);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EnableMonitor = enableMonitor;
                        HistoryCapacity = historyCapacity;
                        MaxCaptureSizeMb = maxCaptureMb;
                        BinaryPreviewKb = binaryPreviewKb;
                        MaskConfidentialHeaders = maskHeaders;
                        ApplyMonitorPreferences();
                    }

                    EditorGUILayout.HelpBox(
                        "UI settings are clamped to safe ranges: history 10-10000 entries, " +
                        "max capture 1-50 MB, binary preview 1-1024 KB.",
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
