using System.Collections.Generic;
using TurboHTTP.Core;
using UnityEngine;
using UnityEngine.Scripting;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Registers the Editor HTTP Monitor with TurboHTTP.Core so that <see cref="UHttpClient"/> can
    /// attach a <see cref="MonitorInterceptor"/> in the Editor without reflection.
    /// </summary>
    /// <remarks>
    /// Runs automatically on entering Play Mode (<c>RuntimeInitializeOnLoadMethod</c>). Without this
    /// module the client appends no monitor. The interceptor is appended only while
    /// <see cref="MonitorInterceptor.CaptureEnabled"/> is set and the client has none configured;
    /// per request it captures only while <see cref="MonitorInterceptor.IsCapturing"/>.
    /// </remarks>
    public static class ObservabilityModuleRegistration
    {
        /// <summary>
        /// Registers the monitor hook. Idempotent.
        /// </summary>
        [Preserve]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void EnsureRegistered()
        {
            EditorMonitorHook.Register(CreateMonitorInterceptor);
        }

        internal static IHttpInterceptor CreateMonitorInterceptor(IReadOnlyList<IHttpInterceptor> configured)
        {
            if (!MonitorInterceptor.CaptureEnabled)
                return null;

            if (configured != null)
            {
                for (int i = 0; i < configured.Count; i++)
                {
                    if (configured[i] is MonitorInterceptor)
                        return null;
                }
            }

            return new MonitorInterceptor();
        }
    }
}
