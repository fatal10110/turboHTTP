using System;
using System.Threading;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// The single sink for TurboHTTP's own diagnostics: failures the library swallows on purpose
    /// (best-effort cleanup, background cache stores, dispose paths) and configuration warnings.
    /// It replaces <c>System.Diagnostics.Debug.WriteLine</c>, which is compiled out of release
    /// builds and has no Unity listener, so those failures were invisible on devices (#180).
    /// </summary>
    /// <remarks>
    /// Policy: nothing is written on success paths, so a correctly configured client is silent.
    /// The default sink writes a warning to the Unity console (<c>UnityEngine.Debug.LogWarning</c>)
    /// in the Editor and in players, and to <see cref="System.Diagnostics.Trace"/> outside Unity.
    /// The sink is replaceable (Observability exposes it publicly); <c>null</c> silences it.
    /// Messages must never carry secrets: callers log exception types, not exception text, URLs or
    /// header values (see <see cref="Describe"/>). A throwing sink is ignored.
    /// The sink and the once-flags are static: they are reset when Unity enters Play Mode, so
    /// Enter Play Mode without domain reload starts from the default.
    /// </remarks>
    internal static class TurboDiagnostics
    {
        /// <summary>Once-flag: FollowRedirects is set but no redirect interceptor is configured (#64).</summary>
        internal const int OnceFollowRedirectsWithoutInterceptor = 1;

        /// <summary>Once-flag: a SAEA channel leaked its buffers because a completion never fired (#168).</summary>
        internal const int OnceSaeaDisposeDrainTimeout = 1 << 4;

        private static readonly Action<string> DefaultSink = WriteDefault;

        private static Action<string> _sink = DefaultSink;
        private static int _onceFlags;

        /// <summary>
        /// Replaces the sink. <c>null</c> silences diagnostics.
        /// </summary>
        internal static void SetSink(Action<string> sink)
        {
            Volatile.Write(ref _sink, sink);
        }

        /// <summary>
        /// Restores the default sink; the once-flags are kept.
        /// </summary>
        internal static void ResetSink()
        {
            Volatile.Write(ref _sink, DefaultSink);
        }

        /// <summary>
        /// Restores the default sink and clears every once-flag.
        /// </summary>
        internal static void Reset()
        {
            Volatile.Write(ref _sink, DefaultSink);
            Volatile.Write(ref _onceFlags, 0);
        }

        /// <summary>
        /// Writes <paramref name="message"/> to the current sink. Never throws.
        /// </summary>
        internal static void Log(string message)
        {
            var sink = Volatile.Read(ref _sink);
            if (sink == null || message == null)
                return;

            try
            {
                sink(message);
            }
            catch
            {
                // A diagnostic sink must never break the cleanup path that reports through it.
            }
        }

        /// <summary>
        /// Writes <paramref name="message"/> followed by the exception type (never the exception
        /// text, which can carry URLs or paths). Never throws.
        /// </summary>
        internal static void Log(string message, Exception exception)
        {
            Log(message + " (" + Describe(exception) + ")");
        }

        /// <summary>
        /// Writes <paramref name="message"/> the first time it is called with <paramref name="onceFlag"/>
        /// since start-up (or the last <see cref="Reset"/>). Returns <c>true</c> when it was written.
        /// </summary>
        internal static bool LogOnce(int onceFlag, string message)
        {
            while (true)
            {
                var current = Volatile.Read(ref _onceFlags);
                if ((current & onceFlag) != 0)
                    return false;

                if (Interlocked.CompareExchange(ref _onceFlags, current | onceFlag, current) == current)
                    break;
            }

            Log(message);
            return true;
        }

        /// <summary>
        /// Secret-free description of an exception: its type name and, one level down, the type of
        /// its inner exception.
        /// </summary>
        internal static string Describe(Exception exception)
        {
            if (exception == null)
                return "unknown error";

            var inner = exception.InnerException;
            return inner == null
                ? exception.GetType().Name
                : exception.GetType().Name + " <- " + inner.GetType().Name;
        }

        private static void WriteDefault(string message)
        {
#if UNITY_5_3_OR_NEWER
            UnityEngine.Debug.LogWarning(message);
#else
            System.Diagnostics.Trace.WriteLine(message);
#endif
        }

#if UNITY_5_3_OR_NEWER
        [UnityEngine.Scripting.Preserve]
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnPlayModeEnter()
        {
            Reset();
        }
#endif
    }
}
