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
        // The BouncyCastle provider, registered by the TurboHTTP.Transport.BouncyCastle assembly at
        // startup (BouncyCastleModuleRegistration, #196). No by-name type lookup: that assembly
        // compiles only when the project defines TURBOHTTP_BOUNCYCASTLE, and a lookup by string is
        // invisible to UnityLinker (#186). Written with Interlocked.Exchange, read with
        // Volatile.Read: registration runs on the main thread, reads on connection threads
        // (ARM64 / 32-bit IL2CPP memory model).
        private static ITlsProvider _bouncyCastleProvider;

        internal const string BouncyCastleDefine = "TURBOHTTP_BOUNCYCASTLE";

        // SslStream handshake viability state, diagnostics only (never changes provider routing).
        // 0 = not yet probed, 1 = viable (handshake succeeded), 2 = platform TLS failure observed.
        // Written by TcpConnectionPool / RawSocketTransport after real TLS handshake attempts.
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
        /// Check if BouncyCastle TLS provider is available without throwing: true once the
        /// TurboHTTP.Transport.BouncyCastle assembly (compiled only when the project defines
        /// <c>TURBOHTTP_BOUNCYCASTLE</c>) has registered its provider.
        /// </summary>
        public static bool IsBouncyCastleAvailable()
        {
            return Volatile.Read(ref _bouncyCastleProvider) != null;
        }

        private static ITlsProvider GetBouncyCastleProvider()
        {
            var provider = Volatile.Read(ref _bouncyCastleProvider);
            if (provider == null)
            {
                throw new InvalidOperationException(
                    "BouncyCastle TLS provider is not available. It is opt-in: add " + BouncyCastleDefine +
                    " to Player Settings > Scripting Define Symbols so the TurboHTTP.Transport.BouncyCastle " +
                    "assembly compiles; it registers its provider at startup (or call " +
                    "BouncyCastleModuleRegistration.EnsureRegistered()).");
            }

            return provider;
        }

        /// <summary>
        /// Registers the BouncyCastle provider. Called by the TurboHTTP.Transport.BouncyCastle
        /// assembly at startup; idempotent. It only makes <see cref="TlsBackend.BouncyCastle"/>
        /// resolvable: <see cref="TlsBackend.Auto"/> never selects it.
        /// </summary>
        internal static void RegisterBouncyCastleProvider(ITlsProvider provider)
        {
            if (provider == null)
                throw new ArgumentNullException(nameof(provider));

            if (!ReferenceEquals(Interlocked.Exchange(ref _bouncyCastleProvider, provider), provider))
                TlsPlatformCapabilities.Invalidate();
        }

        /// <summary>
        /// Replaces the registered BouncyCastle provider (null clears it) and returns the previous
        /// one. Internal, for testing only.
        /// </summary>
        internal static ITlsProvider ExchangeBouncyCastleProviderForTesting(ITlsProvider provider)
        {
            var previous = Interlocked.Exchange(ref _bouncyCastleProvider, provider);
            TlsPlatformCapabilities.Invalidate();
            return previous;
        }

        /// <summary>
        /// Returns true if a previous Auto-mode TLS handshake failed with a platform-level
        /// exception. Diagnostic only (surfaced by <see cref="TlsPlatformCapabilities"/>):
        /// Auto mode never switches providers because of it.
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
        /// Record a platform-level TLS handshake failure for diagnostics.
        /// Uses Exchange (not CompareExchange) so a failure always wins,
        /// even if a concurrent connection succeeded first.
        /// Logs once, on the first transition, with the failure type. The failure still
        /// propagates to the caller: Auto mode never retries on another provider.
        /// </summary>
        internal static void MarkSslStreamBroken(Exception reason = null)
        {
            int prev = Interlocked.Exchange(ref _sslStreamViabilityState, 2);
            if (prev != 2)
                Log("[TurboHTTP TLS] SslStream: platform TLS failure (" +
                    (reason != null ? UnwrapPlatformException(reason).GetType().Name : "unknown") +
                    "); Auto mode does not switch providers, the error is reported to the caller.");
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
        /// Used for diagnostics only; it never selects or switches a TLS provider.
        /// </summary>
        internal static bool IsPlatformTlsException(Exception ex)
        {
            if (ex == null)
                return false;

            ex = UnwrapPlatformException(ex);

            // NotSupportedException is a supertype of PlatformNotSupportedException; it is included
            // because some Unity/Mono runtimes throw the base type from ALPN reflection paths.
            // Known false-positive risk: NotSupportedException from half-closed stream writes is
            // also matched. This only affects the diagnostic label, never provider routing.
            return ex is PlatformNotSupportedException
                || ex is NotSupportedException
                || ex is TypeLoadException
                || ex is TypeInitializationException
                || ex is MissingMethodException
                || ex is EntryPointNotFoundException
                || ex is FileNotFoundException
                || ex is DllNotFoundException;
        }

        private static Exception UnwrapPlatformException(Exception ex)
        {
            // Reflection-invoked calls (MethodInfo.Invoke) wrap the real exception; a single-task
            // AggregateException does the same for Task-based handshakes.
            if (ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null)
                return tie.InnerException;
            if (ex is AggregateException agg && agg.InnerExceptions.Count == 1)
                return agg.InnerExceptions[0];
            return ex;
        }

        /// <summary>
        /// Auto-selection logic (fail closed).
        ///
        /// Auto mode picks the TLS provider before the handshake and never picks one without
        /// full certificate chain validation; a failed handshake (certificate, authentication,
        /// network or platform capability) propagates to the caller with no runtime
        /// catch-and-retry on another provider. BouncyCastle validates chains through
        /// <see cref="ServerCertificateVerifier"/> only in the Editor and on desktop; on iOS and
        /// Android it has no verifier yet and rejects every certificate (#33, #239). Auto therefore
        /// always returns SslStream and throws when it is unavailable; BouncyCastle is used only
        /// when <see cref="TlsBackend.BouncyCastle"/> is selected explicitly.
        /// </summary>
        private static ITlsProvider GetAutoProvider()
        {
            if (TryGetSslStreamProvider(out var sslStreamProvider))
                return sslStreamProvider;

            Log("[TurboHTTP TLS] SslStream unavailable at initialization; " +
                "Auto mode does not fall back to another provider.");
            throw new PlatformNotSupportedException(
                "No usable TLS provider for TlsBackend.Auto: SslStream is unavailable on this runtime, " +
                "and Auto never selects a provider without full certificate chain validation. " +
                "BouncyCastle is used only when TlsBackend.BouncyCastle is selected explicitly.");
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
