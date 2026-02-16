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
            MonitorMiddleware.MaxCaptureSizeBytes = MaxCaptureSizeMb * 1024 * 1024;
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
                    var maxCaptureMb = EditorGUILayout.IntField("Max Capture Size (MB)", MaxCaptureSizeMb);
                    var maskHeaders = EditorGUILayout.Toggle(
                        "Mask Confidential Headers (Default)",
                        MaskConfidentialHeaders);

                    if (EditorGUI.EndChangeCheck())
                    {
                        EnableMonitor = enableMonitor;
                        MaxCaptureSizeMb = maxCaptureMb;
                        MaskConfidentialHeaders = maskHeaders;
                        ApplyMonitorPreferences();
                    }

                    EditorGUILayout.HelpBox(
                        "Text payloads are captured up to the configured max size (default 5 MB). " +
                        "Binary payloads use preview capture to avoid large monitor allocations.",
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
                    "Headers"
                })
            };

            return provider;
        }
    }
}
