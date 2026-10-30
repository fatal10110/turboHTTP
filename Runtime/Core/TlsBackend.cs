namespace TurboHTTP.Core
{
    /// <summary>
    /// TLS backend selection strategy.
    /// </summary>
    public enum TlsBackend
    {
        /// <summary>
        /// Automatically select the TLS provider for the current platform (fail closed).
        ///
        /// Auto picks the provider before the handshake and never picks one without full
        /// certificate chain validation; a failed handshake (certificate, authentication,
        /// network or platform capability) propagates to the caller with no retry on another
        /// provider. BouncyCastle does not yet perform full chain validation, so Auto always
        /// uses SslStream and throws <see cref="System.PlatformNotSupportedException"/> when
        /// SslStream is unavailable; it never falls back to BouncyCastle.
        ///
        /// When the runtime lacks SslStream ALPN APIs, connections that also offer HTTP/1.1
        /// complete a plain TLS handshake and use HTTP/1.1 (no HTTP/2).
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Force use of System.Net.Security.SslStream.
        /// May not support ALPN on all platforms (IL2CPP, mobile); without ALPN,
        /// connections use HTTP/1.1.
        /// </summary>
        SslStream = 1,

        /// <summary>
        /// Force use of BouncyCastle pure C# TLS implementation.
        /// Guaranteed ALPN support on all platforms. Explicit opt-in only: Auto never
        /// selects it while its certificate chain validation is incomplete.
        /// </summary>
        BouncyCastle = 2
    }
}
