namespace TurboHTTP.Core
{
    /// <summary>
    /// TLS backend selection strategy.
    /// </summary>
    public enum TlsBackend
    {
        /// <summary>
        /// Automatically select the best TLS provider for the current platform.
        /// Always prefers SslStream for best performance.
        ///
        /// Fallback to BouncyCastle occurs only for platform capability limitations
        /// (e.g. SslStream ALPN APIs stripped by IL2CPP, or a missing runtime type).
        /// On the first TLS handshake, if SslStream fails with a platform-level exception
        /// (PlatformNotSupportedException, TypeLoadException, etc.), the failure is
        /// cached process-wide and all future connections use BouncyCastle directly.
        ///
        /// Certificate validation failures, authentication errors, hostname mismatches,
        /// and network errors do NOT trigger a BouncyCastle fallback — they are
        /// propagated as exceptions to the caller.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Force use of System.Net.Security.SslStream.
        /// May not support ALPN on all platforms (IL2CPP, mobile).
        /// </summary>
        SslStream = 1,

        /// <summary>
        /// Force use of BouncyCastle pure C# TLS implementation.
        /// Guaranteed ALPN support on all platforms.
        /// </summary>
        BouncyCastle = 2
    }
}
