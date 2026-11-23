using System;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Tcp
{
    /// <summary>The operating-system family the socket close order depends on.</summary>
    internal enum SocketOsFamily
    {
        Unknown = 0,
        Windows = 1,
        /// <summary>macOS and iOS (kqueue).</summary>
        Apple = 2,
        /// <summary>Android and Linux (epoll).</summary>
        Linux = 3,
    }

    /// <summary>The scripting runtime whose socket layer serves the SAEA operations.</summary>
    internal enum SocketRuntimeKind
    {
        /// <summary>Not a Unity scripting backend (e.g. the .NET host of the shadow build).</summary>
        DotNet = 0,
        Mono = 1,
        Il2Cpp = 2,
    }

    /// <summary>
    /// Provides platform-level metadata about the async socket layer, decides the socket close
    /// order per operating system and scripting runtime, and logs the selected
    /// <see cref="SocketIoMode"/> when a transport instance is created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Socket layers by platform:</b>
    /// <list type="table">
    ///   <listheader><term>Platform</term><description>Readiness mechanism of the runtime's socket layer</description></listheader>
    ///   <item><term>Windows (Editor/Standalone)</term><description>Winsock through Mono's or IL2CPP's socket layer; neither uses I/O completion ports for Socket I/O (#843)</description></item>
    ///   <item><term>macOS (Editor/Standalone)</term><description>kqueue</description></item>
    ///   <item><term>iOS</term><description>kqueue</description></item>
    ///   <item><term>Android</term><description>epoll</description></item>
    ///   <item><term>Linux (Standalone)</term><description>epoll</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Close order:</b> chosen per operating system and runtime from what was measured
    /// (<see cref="CloseAbortsPendingIoFor"/>), not from the diagnostic label above.
    /// </para>
    /// <para>
    /// <b>WebGL:</b> The Transport assembly is excluded from WebGL builds via the asmdef
    /// <c>excludePlatforms</c> list. SAEA mode is therefore never available on WebGL.
    /// No runtime detection is needed for that case.
    /// </para>
    /// <para>
    /// Capability checks use compile-time platform and scripting-backend defines and runtime
    /// <see cref="System.Runtime.InteropServices.RuntimeInformation"/> where needed.
    /// No fragile probing loops or socket-level experiments are performed.
    /// </para>
    /// </remarks>
    internal static class SaeaPlatformDetection
    {
        // ── Public metadata ───────────────────────────────────────────────────

        /// <summary>Human-readable name of this platform's socket readiness mechanism (diagnostics only).</summary>
        public static string CompletionModelName => CompletionModelNameFor(CurrentOs);

        /// <summary>
        /// True where the SAEA channel and <see cref="SocketHandleHold"/> close the socket first
        /// (a shutdown is not known to end a pending receive there); false where they shut it down
        /// and close only once the pending operations ended (#384).
        /// </summary>
        internal static bool CloseAbortsPendingIo => CloseAbortsPendingIoFor(CurrentOs, CurrentRuntime);

        /// <summary>The operating system this build runs on.</summary>
        internal static SocketOsFamily CurrentOs
        {
            get
            {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
                return SocketOsFamily.Windows;
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX || UNITY_IOS
                return SocketOsFamily.Apple;
#elif UNITY_ANDROID || UNITY_STANDALONE_LINUX || UNITY_EDITOR_LINUX
                return SocketOsFamily.Linux;
#else
                // Unknown Unity platform or non-Unity host (e.g., test runner on .NET).
                return DetectOsFromRuntime();
#endif
            }
        }

        /// <summary>The scripting runtime this build runs on.</summary>
        internal static SocketRuntimeKind CurrentRuntime
        {
            get
            {
                // In Editor compilations ENABLE_IL2CPP follows the Player Settings scripting
                // backend, but the Editor itself always runs Mono.
#if ENABLE_IL2CPP && !UNITY_EDITOR
                return SocketRuntimeKind.Il2Cpp;
#elif ENABLE_MONO
                return SocketRuntimeKind.Mono;
#else
                return SocketRuntimeKind.DotNet;
#endif
            }
        }

        /// <summary>Diagnostic name of the socket readiness mechanism of <paramref name="os"/>.</summary>
        internal static string CompletionModelNameFor(SocketOsFamily os)
        {
            switch (os)
            {
                case SocketOsFamily.Windows: return "Winsock";
                case SocketOsFamily.Apple: return "kqueue";
                case SocketOsFamily.Linux: return "epoll";
                default: return "unknown";
            }
        }

        /// <summary>
        /// The close order for <paramref name="os"/> and <paramref name="runtime"/>: true closes
        /// first, false shuts down first and closes once the pending operations ended (#384).
        /// </summary>
        /// <remarks>
        /// <list type="bullet">
        ///   <item>Selector platforms (kqueue/epoll), measured on Mono and IL2CPP: a shutdown ends a
        ///   pending receive with the descriptor still open (end of stream, or <c>Interrupted</c>,
        ///   #410), while IL2CPP's close leaves a stale selector job behind (#384). Shut down first.</item>
        ///   <item>Windows, Mono and IL2CPP (measured in the Windows Editor, #405, and in Windows x64
        ///   Mono and IL2CPP players, #843): a local shutdown does not end a pending receive (only the
        ///   peer's close does), so a hold would keep the abandoned read until its bound. A close
        ///   ends the pending receive at once (<c>OperationAborted</c> for SAEA, an
        ///   <c>ObjectDisposedException</c> for Begin/EndReceive), and a new socket that gets the
        ///   closed handle value receives normally: no stale selector job is left. Close first.
        ///   <c>SocketCloseOrderProbeTests</c> checks both facts against this decision.</item>
        ///   <item>Windows, .NET (non-Unity host): the runtime completes Socket I/O through I/O
        ///   completion ports, where a close aborts pending operations. Close first.</item>
        /// </list>
        /// </remarks>
        internal static bool CloseAbortsPendingIoFor(SocketOsFamily os, SocketRuntimeKind runtime)
        {
            if (os != SocketOsFamily.Windows)
                return false;

            switch (runtime)
            {
                case SocketRuntimeKind.Mono:
                    return true; // measured: Windows Editor (#405) and Windows x64 Mono player (#843)
                case SocketRuntimeKind.Il2Cpp:
                    return true; // measured: Windows x64 IL2CPP player (#843)
                default:
                    return true; // .NET on Windows: I/O completion ports
            }
        }

        /// <summary>
        /// False where <see cref="CloseAbortsPendingIoFor"/> answers without a measurement on that
        /// runtime: an unknown operating system. Windows IL2CPP was measured in #843.
        /// </summary>
        internal static bool IsCloseOrderMeasured(SocketOsFamily os, SocketRuntimeKind runtime) =>
            os != SocketOsFamily.Unknown;

        // ── Logging ───────────────────────────────────────────────────────────

        /// <summary>
        /// Emits a single diagnostic line describing the selected socket I/O mode and the
        /// expected completion model. Called once per <see cref="TcpConnectionPool"/> instance.
        /// This is success-path information, so it is compiled in only when the
        /// <c>TURBOHTTP_VERBOSE_DIAGNOSTICS</c> scripting define is set; it then goes to the
        /// diagnostic sink (<see cref="TurboDiagnostics"/>, #180).
        /// </summary>
        /// <param name="mode">The selected <see cref="SocketIoMode"/>.</param>
        [System.Diagnostics.Conditional("TURBOHTTP_VERBOSE_DIAGNOSTICS")]
        public static void LogSelectedMode(SocketIoMode mode)
        {
            string modeLabel;
            switch (mode)
            {
                case SocketIoMode.Saea:
                    modeLabel = $"SAEA ({CompletionModelName}, " +
                                (CloseAbortsPendingIo ? "close first" : "shutdown first") +
                                (IsCloseOrderMeasured(CurrentOs, CurrentRuntime) ? ")" : ", order unmeasured on this runtime)");
                    break;
                case SocketIoMode.PollSelect:
                    modeLabel = "PollSelect (synchronous non-blocking, dedicated poll thread)";
                    break;
                default:
                    modeLabel = "NetworkStream";
                    break;
            }

            TurboDiagnostics.Log($"[TurboHTTP] Socket I/O mode: {modeLabel}");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static SocketOsFamily DetectOsFromRuntime()
        {
            try
            {
                var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
                if (os.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SocketOsFamily.Windows;
                if (os.IndexOf("Darwin", StringComparison.OrdinalIgnoreCase) >= 0)
                    return SocketOsFamily.Apple;
                return SocketOsFamily.Linux;
            }
            catch
            {
                return SocketOsFamily.Unknown;
            }
        }
    }
}
