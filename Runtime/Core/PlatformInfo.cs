using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Provides optimized, allocation-free access to platform and backend information.
    /// Acts as the source of truth for platform-conditional logic in TurboHTTP.
    /// </summary>
    public static class PlatformInfo
    {
        /// <summary>
        /// Returns the current Unity runtime platform.
        /// </summary>
        public static RuntimePlatform Platform => Application.platform;

        /// <summary>
        /// Returns true if running in the Unity Editor.
        /// </summary>
        public static bool IsEditor => Application.isEditor;

        /// <summary>
        /// Returns true if running on a mobile platform (iOS or Android).
        /// </summary>
        public static bool IsMobile
        {
            get
            {
                var p = Platform;
                return p == RuntimePlatform.IPhonePlayer || p == RuntimePlatform.Android;
            }
        }

        /// <summary>
        /// Returns true if running on a desktop standalone platform (Windows, macOS, or Linux).
        /// </summary>
        public static bool IsStandalone
        {
            get
            {
                var p = Platform;
                return p == RuntimePlatform.WindowsPlayer ||
                       p == RuntimePlatform.OSXPlayer ||
                       p == RuntimePlatform.LinuxPlayer;
            }
        }

        /// <summary>
        /// Returns true if the scripting backend is IL2CPP.
        /// Determined at compile time via ENABLE_IL2CPP define.
        /// </summary>
        public static bool IsIL2CPP
        {
            get
            {
#if ENABLE_IL2CPP
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Returns the Unity version string.
        /// </summary>
        public static string UnityVersion => Application.unityVersion;

        /// <summary>
        /// Returns a concise description of the current platform environment.
        /// Format: "[Platform] [Backend] Unity/[Version]"
        /// Example: "OSXPlayer Mono Unity/2022.3.10f1"
        /// </summary>
        public static string GetPlatformDescription()
        {
            // Note: This allocates a string, but it's intended for diagnostics/logging, not hot paths.
            string backend = IsIL2CPP ? "IL2CPP" : "Mono";
            return $"{Platform} {backend} Unity/{UnityVersion}";
        }
    }
}
