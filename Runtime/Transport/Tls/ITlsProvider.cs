using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Abstraction over TLS implementation.
    /// Allows switching between SslStream and BouncyCastle without changing calling code.
    /// </summary>
    public interface ITlsProvider
    {
        /// <summary>
        /// Wrap a raw TCP stream with TLS, performing the handshake.
        /// </summary>
        /// <param name="innerStream">Raw TCP connection stream</param>
        /// <param name="host">Server hostname for SNI and certificate validation</param>
        /// <param name="alpnProtocols">
        /// ALPN protocols to negotiate in order of preference.
        /// Examples: ["h2", "http/1.1"] or ["http/1.1"]
        /// Empty array = no ALPN negotiation.
        /// </param>
        /// <param name="ct">Cancellation token for the handshake operation</param>
        /// <returns>TLS-wrapped stream with negotiation results</returns>
        /// <exception cref="System.Security.Authentication.AuthenticationException">
        /// TLS handshake failed or certificate validation failed
        /// </exception>
        /// <exception cref="IOException">Network error during handshake</exception>
        /// <exception cref="OperationCanceledException">
        /// Handshake was cancelled via cancellation token
        /// </exception>
        Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct);

        /// <summary>
        /// Check if this provider supports ALPN negotiation on the current platform.
        /// </summary>
        /// <remarks>
        /// SslStream may not support ALPN on all platforms (IL2CPP, iOS, Android).
        /// BouncyCastle always returns true.
        /// </remarks>
        bool IsAlpnSupported();

        /// <summary>
        /// Human-readable name of this TLS provider for diagnostics.
        /// Examples: "SslStream", "BouncyCastle"
        /// </summary>
        string ProviderName { get; }
    }
}
