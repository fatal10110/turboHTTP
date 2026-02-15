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
        private static volatile Func<TlsBackend, IHttpTransport> _backendFactory;
        private static volatile Func<TlsBackend, int, IHttpTransport> _advancedFactory;
        private static volatile Lazy<IHttpTransport> _lazy;
        private static readonly object _lock = new object();

        /// <summary>
        /// Register transport factory functions. Creates a new Lazy&lt;T&gt; instance
        /// for thread-safe lazy initialization.
        /// </summary>
        /// <param name="factory">Factory for the default (Auto) transport singleton.</param>
        /// <param name="backendFactory">Optional factory for creating transport instances with a specific TLS backend.</param>
        public static void Register(
            Func<IHttpTransport> factory,
            Func<TlsBackend, IHttpTransport> backendFactory = null)
        {
            Register(factory, backendFactory, advancedFactory: null);
        }

        /// <summary>
        /// Register transport factory functions including advanced option support.
        /// </summary>
        /// <param name="factory">Factory for the default (Auto) transport singleton.</param>
        /// <param name="backendFactory">Optional factory for creating transport instances with a specific TLS backend.</param>
        /// <param name="advancedFactory">Optional factory for creating transport instances with advanced options.</param>
        public static void Register(
            Func<IHttpTransport> factory,
            Func<TlsBackend, IHttpTransport> backendFactory,
            Func<TlsBackend, int, IHttpTransport> advancedFactory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _factory = factory;
                _backendFactory = backendFactory;
                _advancedFactory = advancedFactory;
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
        /// Create a new transport instance with the specified TLS backend.
        /// Unlike <see cref="Default"/>, this creates a fresh (non-singleton) instance
        /// that the caller owns and must dispose.
        /// Falls back to the default factory if no backend-specific factory was registered.
        /// </summary>
        public static IHttpTransport CreateWithBackend(TlsBackend tlsBackend)
        {
            var backendFactory = _backendFactory;
            if (backendFactory != null)
                return backendFactory(tlsBackend);

            // Fallback: no backend-aware factory registered, use default
            var factory = _factory;
            if (factory == null)
                throw new InvalidOperationException(
                    "No transport factory configured. " +
                    "Ensure TurboHTTP.Transport is included in your project.");
            return factory();
        }

        /// <summary>
        /// Create a new transport instance with TLS backend plus advanced transport options.
        /// Unlike <see cref="Default"/>, this creates a fresh (non-singleton) instance
        /// that the caller owns and must dispose.
        /// Falls back to <see cref="CreateWithBackend"/> if no advanced factory was registered.
        /// </summary>
        public static IHttpTransport CreateWithOptions(
            TlsBackend tlsBackend,
            int http2MaxDecodedHeaderBytes)
        {
            var advancedFactory = _advancedFactory;
            if (advancedFactory != null)
                return advancedFactory(tlsBackend, http2MaxDecodedHeaderBytes);

            return CreateWithBackend(tlsBackend);
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
                _backendFactory = null;
                _advancedFactory = null;
                _lazy = null;
            }
        }
    }
}
