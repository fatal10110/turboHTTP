using System;
using System.Threading;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Thread-safe factory for HTTP transport instances.
    /// Uses Lazy&lt;T&gt; for lock-free singleton reads after initialization.
    /// Register a factory via <see cref="Register"/> (typically called by
    /// RawSocketTransport's [ModuleInitializer] in the Transport assembly).
    /// </summary>
    public static class HttpTransportFactory
    {
        private static volatile Func<IHttpTransport> _factory;
        private static volatile Lazy<IHttpTransport> _lazy;
        private static readonly object _lock = new object();

        /// <summary>
        /// Register a transport factory function. Creates a new Lazy&lt;T&gt; instance
        /// for thread-safe lazy initialization. Re-registration is allowed — the
        /// previous singleton (if materialized) becomes eligible for GC. Existing
        /// UHttpClient instances continue using the old singleton; new clients get
        /// the new one via the fresh Lazy&lt;T&gt;.
        /// </summary>
        public static void Register(Func<IHttpTransport> factory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _factory = factory;
                _lazy = new Lazy<IHttpTransport>(_factory, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        /// <summary>
        /// Get the default transport singleton. Lock-free read path — Lazy&lt;T&gt;.Value
        /// is already thread-safe. The lock is only needed during Register().
        /// </summary>
        public static IHttpTransport Default
        {
            get
            {
                var lazy = _lazy;
                if (lazy == null)
                    throw new InvalidOperationException(
                        "No default transport configured. " +
                        "Ensure TurboHTTP.Transport is included in your project. " +
                        "If using IL2CPP and the module initializer did not fire, " +
                        "call RawSocketTransport.EnsureRegistered() at startup.");
                return lazy.Value;
            }
        }

        /// <summary>
        /// Set a transport directly for testing (bypasses factory).
        /// </summary>
        public static void SetForTesting(IHttpTransport transport)
        {
            if (transport == null) throw new ArgumentNullException(nameof(transport));
            lock (_lock)
            {
                _lazy = new Lazy<IHttpTransport>(() => transport, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        /// <summary>
        /// Reset to initial state. For testing only.
        /// Clears both the factory and any created transport.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _factory = null;
                _lazy = null;
            }
        }
    }
}
