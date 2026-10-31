using System;
using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Provides platform-specific configuration defaults and capability flags.
    /// Used to initialize <see cref="UHttpClientOptions"/> with safe, performant defaults.
    /// </summary>
    /// <remarks>
    /// The recommended values are compile-time constants of the build target (iOS and Android
    /// players get the mobile values; the Editor and every other target the desktop values), so
    /// reading them, and constructing <see cref="UHttpClientOptions"/> or
    /// <see cref="ConnectionPoolOptions"/>, performs no reflection and no UnityEngine call and is
    /// safe on any thread.
    /// </remarks>
    public static class PlatformConfig
    {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
        // Mobile: higher tolerance for poor connectivity; conservative concurrency to preserve resources.
        private const int RecommendedTimeoutSeconds = 45;
        private const int RecommendedConcurrency = 8;
#else
        // Desktop/Editor: standard stable connection assumption; higher concurrency allowed.
        private const int RecommendedTimeoutSeconds = 30;
        private const int RecommendedConcurrency = 16;
#endif

        /// <summary>
        /// Recommended request timeout for the build target: 45 s on iOS/Android players,
        /// 30 s in the Editor and on desktop. Mobile networks may require longer timeouts due to
        /// high latency/packet loss.
        /// </summary>
        public static TimeSpan RecommendedTimeout => TimeSpan.FromSeconds(RecommendedTimeoutSeconds);

        /// <summary>
        /// Recommended maximum concurrent requests: 8 on iOS/Android players, 16 in the Editor and
        /// on desktop. Constrained on mobile to reduce thread/socket contention and battery usage.
        /// </summary>
        public static int RecommendedMaxConcurrency => RecommendedConcurrency;

        /// <summary>
        /// Indicates if custom certificate validation is available through TurboHTTP public APIs.
        /// Platform TLS stacks can validate certificates, but custom callbacks are not yet exposed.
        /// </summary>
        public static bool SupportsCustomCertValidation => false;

        /// <summary>
        /// Indicates whether the registered transport reports HTTP/2 negotiation support
        /// (<see cref="TransportCapabilities.Http2"/> in <see cref="HttpTransportFactory.Capabilities"/>).
        /// False when no transport is registered.
        /// </summary>
        public static bool SupportsHttp2 =>
            (HttpTransportFactory.Capabilities & TransportCapabilities.Http2) != 0;

        /// <summary>
        /// Logs the detected platform and recommended configuration to the Unity console.
        /// Intended to be called once at application startup, from the main thread.
        /// </summary>
        public static void LogPlatformInfo()
        {
            Debug.Log($"[TurboHTTP] Initializing on {PlatformInfo.GetPlatformDescription()}\n" +
                      $"Defaults: Timeout={RecommendedTimeout.TotalSeconds}s, Concurrency={RecommendedMaxConcurrency}, " +
                      $"HTTP/2={SupportsHttp2}, CustomCertValidation={SupportsCustomCertValidation}");
        }
    }
}
