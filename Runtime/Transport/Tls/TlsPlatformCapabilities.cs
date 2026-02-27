using System;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Cached diagnostics snapshot describing TLS provider capabilities on the current runtime.
    /// </summary>
    public readonly struct TlsPlatformCapabilitySummary
    {
        public readonly bool IsSystemTlsAvailable;
        public readonly string SystemProviderDescription;
        public readonly bool IsSystemAlpnExpected;
        public readonly bool IsBouncyCastleAvailable;
        public readonly bool IsSystemTlsKnownBroken;
        public readonly TlsBackend RecommendedBackend;

        internal TlsPlatformCapabilitySummary(
            bool isSystemTlsAvailable,
            string systemProviderDescription,
            bool isSystemAlpnExpected,
            bool isBouncyCastleAvailable,
            bool isSystemTlsKnownBroken,
            TlsBackend recommendedBackend)
        {
            IsSystemTlsAvailable = isSystemTlsAvailable;
            SystemProviderDescription = systemProviderDescription ?? "Unavailable";
            IsSystemAlpnExpected = isSystemAlpnExpected;
            IsBouncyCastleAvailable = isBouncyCastleAvailable;
            IsSystemTlsKnownBroken = isSystemTlsKnownBroken;
            RecommendedBackend = recommendedBackend;
        }

        public override string ToString() =>
            $"TLS Capabilities - SystemAvailable={IsSystemTlsAvailable}, " +
            $"SystemProvider={SystemProviderDescription}, " +
            $"SystemAlpnExpected={IsSystemAlpnExpected}, " +
            $"SystemKnownBroken={IsSystemTlsKnownBroken}, " +
            $"BouncyCastleAvailable={IsBouncyCastleAvailable}, " +
            $"Recommended={RecommendedBackend}";
    }

    /// <summary>
    /// Evaluates and caches TLS capability diagnostics for startup logs and tests.
    /// Not used by the request hot path.
    /// </summary>
    public static class TlsPlatformCapabilities
    {
        private static readonly object s_lock = new object();
        // volatile: ensures GetSummary() reads the freshest Lazy<T> reference written by
        // RefreshForTesting() under s_lock. Without volatile, ARM IL2CPP may observe a stale
        // s_cached reference, bypassing a test-triggered refresh. Lazy<ExecutionAndPublication>
        // is itself thread-safe for concurrent .Value reads once a reference is observed.
        private static volatile Lazy<TlsPlatformCapabilitySummary> s_cached = CreateLazy();

        /// <summary>
        /// Returns the cached capability summary.
        /// </summary>
        public static TlsPlatformCapabilitySummary GetSummary() => s_cached.Value;

        /// <summary>
        /// Returns a one-line capability summary suitable for startup diagnostics.
        /// </summary>
        public static string GetDiagnosticSummary() => GetSummary().ToString();

        /// <summary>
        /// Clears the cached capability snapshot so the next <see cref="GetSummary"/>
        /// call re-evaluates the environment. Intended for tests.
        /// </summary>
        internal static void RefreshForTesting()
        {
            lock (s_lock)
            {
                s_cached = CreateLazy();
            }
        }

        private static Lazy<TlsPlatformCapabilitySummary> CreateLazy() =>
            new Lazy<TlsPlatformCapabilitySummary>(
                Evaluate,
                LazyThreadSafetyMode.ExecutionAndPublication);

        private static TlsPlatformCapabilitySummary Evaluate()
        {
            bool isSystemTlsAvailable = TryGetSystemTlsCapabilities(
                out string systemProviderDescription,
                out bool isSystemAlpnExpected);

            bool isBouncyCastleAvailable = TlsProviderSelector.IsBouncyCastleAvailable();
            bool isSystemTlsKnownBroken = TlsProviderSelector.IsSslStreamKnownBroken();

            var recommendedBackend = SelectRecommendedBackend(
                isSystemTlsAvailable,
                isSystemTlsKnownBroken,
                isBouncyCastleAvailable);

            return new TlsPlatformCapabilitySummary(
                isSystemTlsAvailable,
                systemProviderDescription,
                isSystemAlpnExpected,
                isBouncyCastleAvailable,
                isSystemTlsKnownBroken,
                recommendedBackend);
        }

        private static bool TryGetSystemTlsCapabilities(
            out string providerDescription,
            out bool alpnExpected)
        {
            providerDescription = "Unavailable";
            alpnExpected = false;

            try
            {
                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                if (provider == null)
                    return false;

                providerDescription = provider.ProviderName;
                alpnExpected = SafeIsAlpnSupported(provider);
                return true;
            }
            catch (Exception ex) when (TlsProviderSelector.IsPlatformTlsException(ex))
            {
                providerDescription = $"Unavailable ({ex.GetType().Name})";
                return false;
            }
            catch (Exception ex)
            {
                // Conservative evaluation: if probing fails for any reason, do not
                // report system TLS as available.
                providerDescription = $"Unavailable ({ex.GetType().Name})";
                return false;
            }
        }

        private static bool SafeIsAlpnSupported(ITlsProvider provider)
        {
            try
            {
                return provider.IsAlpnSupported();
            }
            catch
            {
                return false;
            }
        }

        private static TlsBackend SelectRecommendedBackend(
            bool systemTlsAvailable,
            bool systemTlsKnownBroken,
            bool bouncyCastleAvailable)
        {
            if (systemTlsAvailable && !systemTlsKnownBroken)
                return TlsBackend.SslStream;

            if (bouncyCastleAvailable)
                return TlsBackend.BouncyCastle;

            // No viable backend confirmed. Return Auto so any attempt will probe and fail
            // with PlatformNotSupportedException rather than silently returning a broken provider.
            return TlsBackend.Auto;
        }
    }
}
