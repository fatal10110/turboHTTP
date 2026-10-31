using System;
using System.Threading;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Thread-safe factory for HTTP transport instances.
    /// Uses Lazy&lt;T&gt; for lock-free singleton reads after initialization.
    /// Register a factory via <see cref="Register"/>. The default raw-socket transport is
    /// registered by TurboHTTP's startup bootstrap (a RuntimeInitializeOnLoadMethod in the
    /// TurboHTTP.Complete assembly) through <c>RawSocketTransport.EnsureRegistered()</c>.
    /// </summary>
    public static class HttpTransportFactory
    {
        private static volatile Func<IHttpTransport> _factory;
        private static volatile Func<TlsBackend, IHttpTransport> _backendFactory;
        private static volatile Func<TlsBackend, ConnectionPoolOptions, Http2Options, IHttpTransport> _advancedFactory;
        private static volatile Func<TlsBackend, ConnectionPoolOptions, Http2Options, StreamingOptions, IHttpTransport> _advancedFactoryWithStreaming;
        private static volatile Lazy<IHttpTransport> _lazy;
        private static volatile Func<TransportCapabilities> _capabilities;
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
            Register(factory, backendFactory, (Func<TlsBackend, ConnectionPoolOptions, Http2Options, IHttpTransport>)null);
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
            Func<TlsBackend, ConnectionPoolOptions, Http2Options, IHttpTransport> advancedFactory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _factory = factory;
                _backendFactory = backendFactory;
                _advancedFactory = advancedFactory;
                _advancedFactoryWithStreaming = null;
                _capabilities = null;
                _lazy = new Lazy<IHttpTransport>(_factory, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        /// <summary>
        /// Register transport factory functions including streaming option support.
        /// </summary>
        public static void Register(
            Func<IHttpTransport> factory,
            Func<TlsBackend, IHttpTransport> backendFactory,
            Func<TlsBackend, ConnectionPoolOptions, Http2Options, StreamingOptions, IHttpTransport> advancedFactory)
        {
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            lock (_lock)
            {
                _factory = factory;
                _backendFactory = backendFactory;
                _advancedFactory = null;
                _advancedFactoryWithStreaming = advancedFactory;
                _capabilities = null;
                _lazy = new Lazy<IHttpTransport>(_factory, LazyThreadSafetyMode.ExecutionAndPublication);
            }
        }

        /// <summary>
        /// Registers how the current transport reports its <see cref="TransportCapabilities"/>.
        /// Call it after <see cref="Register(Func{IHttpTransport}, Func{TlsBackend, IHttpTransport})"/>:
        /// every <c>Register</c> overload and <see cref="Reset"/> clear it, because capabilities belong
        /// to the registered transport. The probe runs on each <see cref="Capabilities"/> read, so it
        /// must be cheap, thread-safe and must not throw; registering it does not run it.
        /// </summary>
        /// <param name="capabilities">Probe returning the transport's capabilities; null clears them.</param>
        public static void RegisterCapabilities(Func<TransportCapabilities> capabilities)
        {
            lock (_lock)
            {
                _capabilities = capabilities;
            }
        }

        /// <summary>
        /// Capabilities the registered transport reports (see <see cref="RegisterCapabilities"/>);
        /// <see cref="TransportCapabilities.None"/> when nothing is registered.
        /// </summary>
        public static TransportCapabilities Capabilities
        {
            get
            {
                var probe = _capabilities;
                return probe == null ? TransportCapabilities.None : probe();
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
                        "Ensure TurboHTTP.Transport and TurboHTTP.Complete are included in the build " +
                        "(the platform must not be WebGL), or call " +
                        "RawSocketTransport.EnsureRegistered() at startup.");
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
            ConnectionPoolOptions poolOptions,
            Http2Options http2Options)
        {
            var advancedFactory = _advancedFactory;
            if (advancedFactory != null)
                return advancedFactory(tlsBackend, poolOptions, http2Options);

            return CreateWithBackend(tlsBackend);
        }

        /// <summary>
        /// Create a new transport instance with TLS backend, connection-pool options,
        /// HTTP/2 options, and streaming-path thresholds.
        /// </summary>
        public static IHttpTransport CreateWithOptions(
            TlsBackend tlsBackend,
            ConnectionPoolOptions poolOptions,
            Http2Options http2Options,
            StreamingOptions streamingOptions)
        {
            var advancedFactoryWithStreaming = _advancedFactoryWithStreaming;
            if (advancedFactoryWithStreaming != null)
                return advancedFactoryWithStreaming(tlsBackend, poolOptions, http2Options, streamingOptions);

            return CreateWithOptions(tlsBackend, poolOptions, http2Options);
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
        /// Detaches the default transport singleton if <see cref="Default"/> has already created
        /// it and returns it so the caller can dispose it; returns null when nothing was created.
        /// The registered factories stay in place, so the next <see cref="Default"/> read creates
        /// a fresh singleton. The startup bootstrap uses this so that Enter Play Mode without a
        /// domain reload disposes the previous session's transport (sockets, HTTP/2 read loops)
        /// instead of leaking it when it registers the defaults again.
        /// </summary>
        internal static IHttpTransport DetachCreatedDefault()
        {
            lock (_lock)
            {
                var lazy = _lazy;
                if (lazy == null || !lazy.IsValueCreated)
                    return null;

                var created = lazy.Value;
                var factory = _factory;
                _lazy = factory != null
                    ? new Lazy<IHttpTransport>(factory, LazyThreadSafetyMode.ExecutionAndPublication)
                    : null;
                return created;
            }
        }

        /// <summary>
        /// Reset to initial state. For testing only.
        /// Clears the factories, the registered capabilities and any created transport.
        /// </summary>
        public static void Reset()
        {
            lock (_lock)
            {
                _factory = null;
                _backendFactory = null;
                _advancedFactory = null;
                _advancedFactoryWithStreaming = null;
                _capabilities = null;
                _lazy = null;
            }
        }
    }
}
