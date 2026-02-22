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
        /// </summary>
        internal static void MarkSslStreamViable() =>
            Interlocked.CompareExchange(ref _sslStreamViabilityState, 1, 0);

        /// <summary>
        /// Mark SslStream as broken after a platform-level TLS handshake failure.
        /// Uses Exchange (not CompareExchange) so a broken result always wins,
        /// even if a concurrent connection succeeded first.
        /// </summary>
        internal static void MarkSslStreamBroken() =>
            Interlocked.Exchange(ref _sslStreamViabilityState, 2);

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

            // Platform TLS unavailable (or failed to initialize): fallback to BouncyCastle.
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
