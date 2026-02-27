using System;
using System.IO;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Selects the appropriate TLS provider based on platform and configuration.
    /// </summary>
    public static class TlsProviderSelector
    {
        // Lazy caching for BouncyCastle provider - avoids reflection overhead on every call
        private static readonly Lazy<ITlsProvider> _bouncyCastleProvider =
            new Lazy<ITlsProvider>(LoadBouncyCastleProvider, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        // Thread-safe availability check cache (avoids non-atomic nullable writes on 32-bit IL2CPP).
        private static readonly Lazy<bool> _bouncyCastleAvailable =
            new Lazy<bool>(DetectBouncyCastleAvailability, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

        // SslStream handshake viability probe state.
        // 0 = not yet probed, 1 = viable (handshake succeeded), 2 = broken (platform exception).
        // Written by TcpConnectionPool after the first real TLS handshake attempt.
        private static int _sslStreamViabilityState;

        // Volatile backing field ensures writes from the main thread are visible to
        // connection pool threads on ARM IL2CPP without requiring a lock on every read.
        private static Action<string> _diagnosticLogger;

        /// <summary>
        /// Optional diagnostic logger. When set, receives one-line messages describing
        /// TLS provider selection events (first-use selection and capability fallbacks).
        /// Defaults to null (silent).
        /// </summary>
        /// <remarks>
        /// Set once during application startup, before any TLS connections are established.
        /// Setting this concurrently with active connections may cause sporadic missed log
        /// messages on the first provider transition. Example usage:
        /// <c>TlsProviderSelector.DiagnosticLogger = msg => Debug.Log(msg);</c>
        /// </remarks>
        public static Action<string> DiagnosticLogger
        {
            get => Volatile.Read(ref _diagnosticLogger);
            set => Volatile.Write(ref _diagnosticLogger, value);
        }

        private static void Log(string message) => DiagnosticLogger?.Invoke(message);

        /// <summary>
        /// Get the TLS provider for the specified backend strategy.
        /// </summary>
        public static ITlsProvider GetProvider(TlsBackend backend = TlsBackend.Auto)
        {
            switch (backend)
            {
                case TlsBackend.SslStream:
                    return GetSslStreamProvider();

                case TlsBackend.BouncyCastle:
                    return GetBouncyCastleProvider();

                case TlsBackend.Auto:
                default:
                    return GetAutoProvider();
            }
        }

        private static ITlsProvider GetSslStreamProvider()
        {
            return SslStreamTlsProvider.Instance;
        }

        /// <summary>
        /// Check if BouncyCastle TLS provider is available without throwing.
        /// </summary>
        public static bool IsBouncyCastleAvailable()
        {
            return _bouncyCastleAvailable.Value;
        }

        private static ITlsProvider GetBouncyCastleProvider()
        {
            if (!IsBouncyCastleAvailable())
            {
                throw new InvalidOperationException(
                    "BouncyCastle TLS provider is not available. " +
                    "Ensure TurboHTTP.Transport.BouncyCastle assembly is included in your project.");
            }
            
            return _bouncyCastleProvider.Value;
        }
        
        private static ITlsProvider LoadBouncyCastleProvider()
        {
            // Use reflection to load BouncyCastle provider
            // This allows the BouncyCastle module to be optional
            // Note: Requires [Preserve] attribute on BouncyCastleTlsProvider for IL2CPP
            var bcType = Type.GetType(
                "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
                throwOnError: true);

            // Get singleton instance via reflection
            // Note: Instance is a 'static readonly' field, not a property
            var instanceField = bcType.GetField("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (instanceField == null)
            {
                throw new InvalidOperationException(
                    $"BouncyCastle TLS provider type '{bcType.FullName}' does not have a public static 'Instance' field. " +
                    "This indicates a version mismatch or corrupted assembly.");
            }

            return (ITlsProvider)instanceField.GetValue(null);
        }

        private static bool DetectBouncyCastleAvailability()
        {
            var bcType = Type.GetType(
                "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
                throwOnError: false);

            return bcType != null;
        }

        /// <summary>
        /// Returns true if a previous TLS handshake marked SslStream as broken on this platform.
        /// Used by TcpConnectionPool to skip SslStream and go directly to BouncyCastle.
        /// </summary>
        internal static bool IsSslStreamKnownBroken() =>
            Volatile.Read(ref _sslStreamViabilityState) == 2;

        /// <summary>
        /// Mark SslStream as viable after a successful TLS handshake.
        /// Only transitions from unknown (0) to viable (1); does not overwrite a broken (2) state.
        /// Logs at most once (on the first 0→1 transition).
        /// </summary>
        internal static void MarkSslStreamViable()
        {
            if (Interlocked.CompareExchange(ref _sslStreamViabilityState, 1, 0) == 0)
                Log("[TurboHTTP TLS] Provider selected: SslStream (first successful handshake).");
        }

        /// <summary>
        /// Mark SslStream as broken after a platform-level TLS handshake failure.
        /// Uses Exchange (not CompareExchange) so a broken result always wins,
        /// even if a concurrent connection succeeded first.
        /// Logs on first transition to broken state.
        /// </summary>
        internal static void MarkSslStreamBroken()
        {
            int prev = Interlocked.Exchange(ref _sslStreamViabilityState, 2);
            if (prev != 2)
                Log("[TurboHTTP TLS] SslStream: platform capability failure detected; " +
                    "Auto mode will use BouncyCastle for all future connections.");
        }

        /// <summary>
        /// Reset probe state. Internal, for testing only.
        /// </summary>
        internal static void ResetProbeState() =>
            Interlocked.Exchange(ref _sslStreamViabilityState, 0);

        /// <summary>
        /// Returns true if the given exception indicates a platform-level TLS failure
        /// (SslStream is fundamentally broken on this runtime), as opposed to a
        /// normal handshake error (bad cert, network down, timeout).
        /// </summary>
        internal static bool IsPlatformTlsException(Exception ex)
        {
            // Unwrap reflection-invoked exceptions (e.g., from AuthenticateWithAlpnAsync
            // which uses MethodInfo.Invoke — platform exceptions get wrapped).
            if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                ex = tie.InnerException;

            // NotSupportedException is a supertype of PlatformNotSupportedException; it is included
            // because some Unity/Mono runtimes throw the base type from ALPN reflection paths.
            // Known false-positive risk: NotSupportedException from half-closed stream writes is
            // also caught here. This is acceptable because IsPlatformTlsException is only called
            // from WrapAsync (handshake context), where NotSupportedException reliably indicates
            // a capability gap rather than an I/O state error.
            return ex is PlatformNotSupportedException
                || ex is NotSupportedException
                || ex is TypeLoadException
                || ex is TypeInitializationException
                || ex is MissingMethodException
                || ex is EntryPointNotFoundException
                || ex is FileNotFoundException
                || ex is DllNotFoundException;
        }

        /// <summary>
        /// Platform-specific auto-selection logic.
        ///
        /// Auto mode always prefers platform TLS (SslStream) for best performance.
        /// If a prior handshake proved SslStream is broken on this runtime, returns
        /// BouncyCastle directly (zero overhead after the first probe).
        /// BouncyCastle is used only when SslStream cannot be initialized on this runtime.
        /// </summary>
        private static ITlsProvider GetAutoProvider()
        {
            // Fast path: if a prior handshake already proved SslStream broken, skip it.
            if (IsSslStreamKnownBroken())
                return GetBouncyCastleFallbackOrThrow();

            if (TryGetSslStreamProvider(out var sslStreamProvider))
                return sslStreamProvider;

            // Platform TLS unavailable at initialization (before any real handshake).
            Log("[TurboHTTP TLS] SslStream unavailable at initialization; " +
                "Auto mode will use BouncyCastle.");
            return GetBouncyCastleFallbackOrThrow();
        }

        private static ITlsProvider GetBouncyCastleFallbackOrThrow()
        {
            if (IsBouncyCastleAvailable())
            {
                try
                {
                    return GetBouncyCastleProvider();
                }
                catch (InvalidOperationException ex)
                {
                    throw new PlatformNotSupportedException(
                        "No usable TLS provider found. SslStream is unavailable and BouncyCastle failed to initialize.",
                        ex);
                }
            }

            throw new PlatformNotSupportedException(
                "No usable TLS provider found. SslStream is unavailable and BouncyCastle is not present.");
        }

        private static bool TryGetSslStreamProvider(out ITlsProvider provider)
        {
            try
            {
                provider = GetSslStreamProvider();
                return provider != null;
            }
            catch (Exception ex) when (IsPlatformTlsException(ex))
            {
                provider = null;
                return false;
            }
        }
    }
}
