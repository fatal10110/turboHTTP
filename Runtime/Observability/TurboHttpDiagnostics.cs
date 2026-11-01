using System;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Routes TurboHTTP's own diagnostics: failures the library swallows on purpose (best-effort
    /// cleanup, background cache stores, dispose paths) and one-time configuration warnings.
    /// </summary>
    /// <remarks>
    /// Nothing is written on success paths. By default each diagnostic is a warning in the Unity
    /// console (Editor and players) and goes to <see cref="System.Diagnostics.Trace"/> outside
    /// Unity. Messages name exception types only, never exception text, URLs or header values.
    /// The sink is reset to the default whenever Unity enters Play Mode, so install a custom sink
    /// from a <c>BeforeSceneLoad</c> (or later) initializer.
    /// </remarks>
    public static class TurboHttpDiagnostics
    {
        /// <summary>
        /// Replaces the diagnostic sink. Pass <c>null</c> to silence diagnostics. The sink may be
        /// called from any thread, including thread-pool and finalizer threads, and must be
        /// thread-safe; exceptions it throws are ignored.
        /// </summary>
        /// <param name="sink">Receives each diagnostic message, or <c>null</c> to discard them.</param>
        public static void SetSink(Action<string> sink)
        {
            TurboDiagnostics.SetSink(sink);
        }

        /// <summary>
        /// Restores the default sink (Unity console warning, or <c>Trace</c> outside Unity).
        /// </summary>
        public static void ResetSink()
        {
            TurboDiagnostics.ResetSink();
        }
    }
}
