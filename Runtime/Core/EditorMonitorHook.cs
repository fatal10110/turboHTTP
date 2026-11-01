using System;
using System.Collections.Generic;
using System.Threading;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Registration point through which the optional Observability module makes the Editor HTTP
    /// Monitor known to <see cref="UHttpClient"/> without reflection (#186). Absent registration
    /// is a no-op: no interceptor is appended.
    /// </summary>
    internal static class EditorMonitorHook
    {
        private static Func<IReadOnlyList<IHttpInterceptor>, IHttpInterceptor> _factory;

        /// <summary>
        /// Registers (or with <c>null</c> clears) the factory. The factory receives the interceptors
        /// already configured for the client and returns the monitor interceptor to append, or
        /// <c>null</c> when none should be appended (capture disabled, or one is already present).
        /// </summary>
        internal static void Register(Func<IReadOnlyList<IHttpInterceptor>, IHttpInterceptor> factory)
        {
            Volatile.Write(ref _factory, factory);
        }

        internal static bool IsRegistered => Volatile.Read(ref _factory) != null;

        /// <summary>
        /// Appends the registered monitor interceptor to <paramref name="interceptors"/>. Returns
        /// <c>true</c> when one was appended. Never throws: monitor auto-wiring is optional and must
        /// not break client construction.
        /// </summary>
        internal static bool TryAppend(List<IHttpInterceptor> interceptors)
        {
            var factory = Volatile.Read(ref _factory);
            if (factory == null || interceptors == null)
                return false;

            IHttpInterceptor interceptor;
            try
            {
                interceptor = factory(interceptors);
            }
            catch (Exception ex)
            {
                TurboDiagnostics.Log("[TurboHTTP] Editor monitor hook failed", ex);
                return false;
            }

            if (interceptor == null)
                return false;

            interceptors.Add(interceptor);
            return true;
        }
    }
}
