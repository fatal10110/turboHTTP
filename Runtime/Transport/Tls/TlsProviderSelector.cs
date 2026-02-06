using System;
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
        
        // Track if BouncyCastle is available (null = not checked, true/false = cached result)
        private static bool? _bouncyCastleAvailable;

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
            if (_bouncyCastleAvailable.HasValue)
                return _bouncyCastleAvailable.Value;
            
            var bcType = Type.GetType(
                "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
                throwOnError: false);
            
            _bouncyCastleAvailable = bcType != null;
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
            var instanceProperty = bcType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            if (instanceProperty == null)
            {
                throw new InvalidOperationException(
                    $"BouncyCastle TLS provider type '{bcType.FullName}' does not have a public static 'Instance' property. " +
                    "This indicates a version mismatch or corrupted assembly.");
            }

            return (ITlsProvider)instanceProperty.GetValue(null);
        }

        private static ITlsProvider GetAutoProvider()
        {
            // Platform-specific auto-selection logic
#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
            // Desktop platforms: SslStream works reliably
            return GetSslStreamProvider();

#elif UNITY_IOS || UNITY_ANDROID
            // Mobile platforms: Check if SslStream supports ALPN
            var sslStreamProvider = SslStreamTlsProvider.Instance;
            if (sslStreamProvider.IsAlpnSupported())
            {
                // ALPN works via reflection, use SslStream
                return sslStreamProvider;
            }
            else
            {
                // ALPN not supported, fall back to BouncyCastle
                try
                {
                    return GetBouncyCastleProvider();
                }
                catch (InvalidOperationException)
                {
                    // BouncyCastle not available, use SslStream anyway
                    // (ALPN will be null, HTTP/1.1 fallback)
#if UNITY_2017_1_OR_NEWER
                    UnityEngine.Debug.LogWarning(
                        "BouncyCastle TLS provider not available. " +
                        "ALPN negotiation may not work. HTTP/2 will be unavailable.");
#else
                    System.Diagnostics.Debug.WriteLine(
                        "[TurboHTTP] WARNING: BouncyCastle TLS provider not available. " +
                        "ALPN negotiation may not work. HTTP/2 will be unavailable.");
#endif
                    return sslStreamProvider;
                }
            }

#elif UNITY_STANDALONE_LINUX
            // Linux: Prefer BouncyCastle due to Mono inconsistencies
            try
            {
                return GetBouncyCastleProvider();
            }
            catch (InvalidOperationException)
            {
                // BouncyCastle not available, fall back to SslStream
                return GetSslStreamProvider();
            }

#else
            // Unknown platform or non-Unity: Try SslStream
            return GetSslStreamProvider();
#endif
        }
    }
}
