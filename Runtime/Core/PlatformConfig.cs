using System;
using System.Reflection;
using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Provides platform-specific configuration defaults and capability flags.
    /// Used to initialize <see cref="UHttpClientOptions"/> with safe, performant defaults.
    /// </summary>
    public static class PlatformConfig
    {
        private static readonly bool _bouncyCastleAvailable = Type.GetType(
            "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
            throwOnError: false) != null;

        private static readonly bool _sslStreamAlpnApisAvailable = DetectSslStreamAlpnSupport();

        /// <summary>
        /// Recommended request timeout based on the current platform.
        /// Mobile networks may require longer timeouts due to high latency/packet loss.
        /// </summary>
        public static TimeSpan RecommendedTimeout => PlatformInfo.IsMobile
            ? TimeSpan.FromSeconds(45) // Mobile: Higher tolerance for poor connectivity
            : TimeSpan.FromSeconds(30); // Desktop/Editor: Standard stable connection assumption

        /// <summary>
        /// Recommended maximum concurrent requests.
        /// Constrained on mobile to reduce thread/socket contention and battery usage.
        /// </summary>
        public static int RecommendedMaxConcurrency => PlatformInfo.IsMobile
            ? 8  // Mobile: Conservative limit to preserve resources
            : 16; // Desktop: Higher concurrency allowed

        /// <summary>
        /// Indicates if custom certificate validation is available through TurboHTTP public APIs.
        /// Platform TLS stacks can validate certificates, but custom callbacks are not yet exposed.
        /// </summary>
        public static bool SupportsCustomCertValidation => false;

        /// <summary>
        /// Indicates whether HTTP/2 negotiation support is available.
        /// This reports capability based on bundled providers and exposed ALPN APIs.
        /// </summary>
        public static bool SupportsHttp2 => _bouncyCastleAvailable || _sslStreamAlpnApisAvailable;

        /// <summary>
        /// Logs the detected platform and recommended configuration to the Unity console.
        /// Intended to be called once at application startup.
        /// </summary>
        public static void LogPlatformInfo()
        {
            Debug.Log($"[TurboHTTP] Initializing on {PlatformInfo.GetPlatformDescription()}\n" +
                      $"Defaults: Timeout={RecommendedTimeout.TotalSeconds}s, Concurrency={RecommendedMaxConcurrency}, " +
                      $"HTTP/2={SupportsHttp2}, CustomCertValidation={SupportsCustomCertValidation}");
        }

        private static bool DetectSslStreamAlpnSupport()
        {
            // Reuse Transport's SslStream ALPN capability detection to avoid
            // duplicating reflection logic in Core.
            var providerType = Type.GetType(
                "TurboHTTP.Transport.Tls.SslStreamTlsProvider, TurboHTTP.Transport",
                throwOnError: false);
            if (providerType == null)
                return false;

            var instanceField = providerType.GetField(
                "Instance",
                BindingFlags.Public | BindingFlags.Static);
            var isAlpnSupportedMethod = providerType.GetMethod(
                "IsAlpnSupported",
                BindingFlags.Public | BindingFlags.Instance);

            if (instanceField == null || isAlpnSupportedMethod == null)
                return false;

            var instance = instanceField.GetValue(null);
            if (instance == null)
                return false;

            if (isAlpnSupportedMethod.Invoke(instance, null) is bool supported)
                return supported;

            return false;
        }
    }
}
