using System;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>
    /// Provides platform-level metadata about the expected async socket completion model,
    /// and exposes a method to log the selected <see cref="SocketIoMode"/> when a
    /// transport instance is created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Completion models by platform:</b>
    /// <list type="table">
    ///   <listheader><term>Platform</term><description>Async I/O mechanism</description></listheader>
    ///   <item><term>Windows (Editor/Standalone)</term><description>IOCP (I/O Completion Ports)</description></item>
    ///   <item><term>macOS (Editor/Standalone)</term><description>kqueue</description></item>
    ///   <item><term>iOS</term><description>kqueue</description></item>
    ///   <item><term>Android</term><description>epoll</description></item>
    ///   <item><term>Linux (Standalone)</term><description>epoll</description></item>
    /// </list>
    /// All supported platforms use native kernel-level async socket APIs, so SAEA mode
    /// provides genuine zero-allocation benefits on all of them.
    /// </para>
    /// <para>
    /// <b>WebGL:</b> The Transport assembly is excluded from WebGL builds via the asmdef
    /// <c>excludePlatforms</c> list. SAEA mode is therefore never available on WebGL.
    /// No runtime detection is needed for that case.
    /// </para>
    /// <para>
    /// Capability checks use compile-time platform defines and runtime
    /// <see cref="System.Runtime.InteropServices.RuntimeInformation"/> where needed.
    /// No fragile probing loops or socket-level experiments are performed.
    /// </para>
    /// </remarks>
    internal static class SaeaPlatformDetection
    {
        // ── Public metadata ───────────────────────────────────────────────────

        /// <summary>
        /// Human-readable name of the async I/O completion model expected on this platform.
        /// Used for diagnostics only.
        /// </summary>
        public static string CompletionModelName
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return "IOCP";
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
                return "kqueue";
#elif UNITY_ANDROID || UNITY_STANDALONE_LINUX
                return "epoll";
#else
                // Unknown Unity platform or non-Unity host (e.g., test runner on .NET).
                // Map via RuntimeInformation to give a useful diagnostic string.
                return DetectFromRuntime();
#endif
            }
        }

        // ── Logging ───────────────────────────────────────────────────────────

        /// <summary>
        /// Emits a single diagnostic line describing the selected socket I/O mode and the
        /// expected completion model. Called once per <see cref="TcpConnectionPool"/> instance.
        /// Uses <see cref="System.Diagnostics.Debug.WriteLine"/> so the output appears in the
        /// Unity Editor Console (Development builds) without a hard Unity dependency.
        /// </summary>
        /// <param name="mode">The selected <see cref="SocketIoMode"/>.</param>
        public static void LogSelectedMode(SocketIoMode mode)
        {
            string modeLabel;
            switch (mode)
            {
                case SocketIoMode.Saea:
                    modeLabel = $"SAEA ({CompletionModelName})";
                    break;
                case SocketIoMode.PollSelect:
                    modeLabel = "PollSelect (synchronous non-blocking, dedicated poll thread)";
                    break;
                default:
                    modeLabel = "NetworkStream";
                    break;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[TurboHTTP] Socket I/O mode: {modeLabel}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string DetectFromRuntime()
        {
            try
            {
                var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                if (os.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "IOCP";
                if (os.IndexOf("Darwin", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "kqueue";
                return "epoll";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}
