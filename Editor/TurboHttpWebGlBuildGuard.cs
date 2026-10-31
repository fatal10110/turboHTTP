using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace TurboHTTP.Editor
{
    /// <summary>
    /// Fails WebGL player builds at build time. TurboHTTP's transport runs on raw
    /// <c>System.Net.Sockets</c>, which browsers do not provide, so a WebGL build would compile and
    /// then fail every request at runtime.
    /// </summary>
    internal sealed class TurboHttpWebGlBuildGuard : IPreprocessBuildWithReport
    {
        internal const string UnsupportedMessage =
            "TurboHTTP does not support WebGL in v1.0: its transport uses raw sockets, which browsers " +
            "do not provide, so every request would fail at runtime. Build for Standalone, iOS or " +
            "Android, or remove the TurboHTTP package (com.turbohttp.complete) from WebGL projects. " +
            "See Documentation~/PlatformNotes.md.";

        // Run before other preprocessors so the build stops before any expensive step.
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            ThrowIfUnsupported(report.summary.platform);
        }

        /// <summary>Throws <see cref="BuildFailedException"/> when <paramref name="target"/> is unsupported.</summary>
        internal static void ThrowIfUnsupported(BuildTarget target)
        {
            if (target == BuildTarget.WebGL)
                throw new BuildFailedException(UnsupportedMessage);
        }
    }
}
