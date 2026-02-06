namespace TurboHTTP.Core
{
    /// <summary>
    /// TLS backend selection strategy.
    /// </summary>
    public enum TlsBackend
    {
        /// <summary>
        /// Automatically select the best TLS provider for the current platform.
        /// Uses SslStream on desktop platforms; falls back to BouncyCastle on mobile/IL2CPP
        /// if ALPN support is not available.
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
