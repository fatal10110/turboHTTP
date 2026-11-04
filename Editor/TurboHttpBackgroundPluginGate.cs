using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Keeps the experimental background networking plugins (the Android
    /// <c>TurboHttpBackground.androidlib</c> with its WorkManager dependency, and the iOS
    /// <c>TurboHttpBackgroundTaskBridge.mm</c>) out of player builds unless the build target's Player
    /// Settings define <see cref="Define"/> (#89, #106). The managed bridges compile their native calls
    /// under the same define, so native code and its callers are always built in together.
    /// </summary>
    /// <remarks>
    /// Unity 2021.3 does not apply an importer's define constraints to native plugins (an Android build
    /// without the define still packaged the .androidlib), so the gate uses
    /// <see cref="PluginImporter.SetIncludeInBuildDelegate"/>. It is registered after every domain reload
    /// and again before each build. Defines passed only through <c>BuildPlayerOptions.extraScriptingDefines</c>
    /// are not seen here: set the define in Player Settings.
    /// </remarks>
    [InitializeOnLoad]
    internal sealed class TurboHttpBackgroundPluginGate : IPreprocessBuildWithReport
    {
        /// <summary>Scripting define that opts a player build into background networking.</summary>
        internal const string Define = "TURBOHTTP_BACKGROUND_NETWORKING";

        /// <summary>Package-relative paths of the gated plugins.</summary>
        internal static readonly string[] GatedPluginPaths =
        {
            "Plugins/Android/TurboHttpBackground.androidlib",
            "Plugins/iOS/TurboHttpBackgroundTaskBridge.mm",
        };

        static TurboHttpBackgroundPluginGate()
        {
            Register();
        }

        // Run before Unity collects plugins, and before other TurboHTTP preprocessors except the WebGL guard.
        public int callbackOrder => -900;

        public void OnPreprocessBuild(BuildReport report)
        {
            Register();
        }

        /// <summary>Installs the include delegate on every gated plugin importer in the project.</summary>
        internal static int Register()
        {
            var registered = 0;
            foreach (var importer in PluginImporter.GetAllImporters())
            {
                if (importer == null || !IsGatedPlugin(importer.assetPath))
                    continue;

                importer.SetIncludeInBuildDelegate(ShouldIncludeInBuild);
                registered++;
            }

            return registered;
        }

        /// <summary><see langword="true"/> when <paramref name="assetPath"/> is one of the gated plugins.</summary>
        internal static bool IsGatedPlugin(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;

            var normalized = assetPath.Replace('\\', '/');
            foreach (var gated in GatedPluginPaths)
            {
                if (normalized.EndsWith("/" + gated, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary><see langword="true"/> when the ';'-separated <paramref name="symbols"/> contain <see cref="Define"/>.</summary>
        internal static bool IsDefined(string symbols)
        {
            if (string.IsNullOrEmpty(symbols))
                return false;

            foreach (var symbol in symbols.Split(';'))
            {
                if (string.Equals(symbol.Trim(), Define, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static bool ShouldIncludeInBuild(string assetPath)
        {
            var group = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget);
            return IsDefined(PlayerSettings.GetScriptingDefineSymbolsForGroup(group));
        }
    }
}
