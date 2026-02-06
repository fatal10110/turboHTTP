using System;
using System.IO;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Result of a TLS handshake, including the secured stream and negotiation metadata.
    /// </summary>
    public sealed class TlsResult
    {
        /// <summary>
        /// The TLS-secured stream. Use this for all subsequent communication.
        /// DO NOT use the original innerStream passed to WrapAsync.
        /// </summary>
        public Stream SecureStream { get; }

        /// <summary>
        /// The ALPN protocol negotiated during the handshake.
        /// Common values: "h2" (HTTP/2), "http/1.1" (HTTP/1.1), or null (no ALPN).
        /// </summary>
        /// <remarks>
        /// null means:
        ///  - ALPN was not requested (empty alpnProtocols array), OR
        ///  - Server doesn't support ALPN extension, OR
        ///  - No common protocol was found between client and server
        /// </remarks>
        public string NegotiatedAlpn { get; }

        /// <summary>
        /// TLS protocol version negotiated.
        /// Common values: "1.2", "1.3"
        /// </summary>
        public string TlsVersion { get; }

        /// <summary>
        /// Cipher suite negotiated (optional, may be null if provider doesn't expose it).
        /// Example: "TLS_AES_128_GCM_SHA256"
        /// </summary>
        /// <remarks>
        /// This is primarily for diagnostics and logging.
        /// SslStream may not expose this on all platforms; BouncyCastle can provide it.
        /// </remarks>
        public string CipherSuite { get; }

        /// <summary>
        /// Name of the TLS provider that performed the handshake.
        /// Examples: "SslStream", "BouncyCastle"
        /// </summary>
        public string ProviderName { get; }

        public TlsResult(
            Stream secureStream,
            string negotiatedAlpn,
            string tlsVersion,
            string cipherSuite = null,
            string providerName = null)
        {
            SecureStream = secureStream ?? throw new ArgumentNullException(nameof(secureStream));
            NegotiatedAlpn = negotiatedAlpn;  // null is valid
            TlsVersion = tlsVersion ?? throw new ArgumentNullException(nameof(tlsVersion));
            CipherSuite = cipherSuite;
            ProviderName = providerName ?? "Unknown";
        }
    }
}
