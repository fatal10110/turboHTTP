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
        /// Unity's SslStream (Editor and players) cannot negotiate ALPN, so Auto connections
        /// complete a plain TLS handshake and use HTTP/1.1 (no HTTP/2). HTTP/2 on Unity needs
        /// <see cref="BouncyCastle"/>.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Force use of System.Net.Security.SslStream.
        /// Unity's SslStream (Editor and players, Mono and IL2CPP) has no ALPN, so connections
        /// use HTTP/1.1, and a request that offers only h2 fails with
        /// <see cref="System.PlatformNotSupportedException"/>.
        /// </summary>
        SslStream = 1,

        /// <summary>
        /// Force use of BouncyCastle pure C# TLS implementation.
        /// Negotiates ALPN on all platforms: the backend HTTP/2 needs on Unity. Explicit opt-in
        /// only: Auto never selects it.
        /// Available only when the project defines the <c>TURBOHTTP_BOUNCYCASTLE</c> scripting
        /// define symbol (Player Settings &gt; Scripting Define Symbols, per build target), which
        /// compiles the BouncyCastle glue assembly. Without it, every TLS connection with this
        /// backend fails: the provider lookup throws <see cref="System.InvalidOperationException"/>
        /// naming the define (requests receive it as the inner exception of a
        /// <see cref="UHttpException"/>).
        /// </summary>
        BouncyCastle = 2
    }
}
