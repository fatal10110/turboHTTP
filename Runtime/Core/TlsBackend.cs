namespace TurboHTTP.Core
{
    /// <summary>
    /// TLS backend selection strategy.
    /// </summary>
    public enum TlsBackend
    {
        /// <summary>
        /// Automatically select the best TLS provider for the current platform.
        /// Always prefers SslStream for best performance. On the first TLS handshake,
        /// if SslStream fails with a platform-level exception, caches the failure and
        /// falls back to BouncyCastle for all future connections in this process.
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
