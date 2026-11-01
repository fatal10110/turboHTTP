

using System;
using System.Collections.Generic;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// BouncyCastle TLS client implementation with ALPN support for HTTP/2.
    /// </summary>
    /// <remarks>
    /// Security-relevant choices are explicit overrides here so they hold whatever the vendored
    /// library's defaults are (#142): the AEAD/ECDHE suite list, no OCSP stapling request (revocation
    /// is off by decision, #33), close_notify required, and SNI per RFC 6066 section 3.
    /// </remarks>
    internal sealed class TurboTlsClient : DefaultTlsClient
    {
        /// <summary>
        /// Default bound for the whole handshake, including certificate evaluation. The provider
        /// enforces it (BouncyCastle's own check runs only in blocking mode); the request timeout
        /// still applies on top, so the effective bound is the smaller of the two.
        /// </summary>
        internal const int DefaultHandshakeTimeoutMillis = 30000;

        /// <summary>
        /// Offered cipher suites in preference order (RFC 8446 B.4, RFC 5289, RFC 7905): TLS 1.3
        /// AEAD suites, then TLS 1.2 ECDHE with AEAD only. No static-RSA key exchange, no CBC, no
        /// DHE. The engine appends TLS_EMPTY_RENEGOTIATION_INFO_SCSV (0x00FF, RFC 5746) on the wire.
        /// </summary>
        internal static readonly int[] OfferedCipherSuites =
        {
            CipherSuite.TLS_CHACHA20_POLY1305_SHA256,
            CipherSuite.TLS_AES_128_GCM_SHA256,
            CipherSuite.TLS_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
            CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
            CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        };

        private readonly string _targetHost;
        private readonly string[] _alpnProtocols;
        private readonly int _handshakeTimeoutMillis;

        /// <summary>
        /// ALPN protocol negotiated by the server, or null if no match.
        /// </summary>
        public string NegotiatedAlpn { get; private set; }

        /// <summary>
        /// TLS version negotiated (e.g., "1.2", "1.3").
        /// </summary>
        public string NegotiatedVersion { get; private set; }

        /// <summary>
        /// Cipher suite negotiated (for diagnostics).
        /// </summary>
        public string NegotiatedCipherSuite { get; private set; }

        public TurboTlsClient(TlsCrypto crypto, string targetHost, string[] alpnProtocols)
            : this(crypto, targetHost, alpnProtocols, DefaultHandshakeTimeoutMillis)
        {
        }

        public TurboTlsClient(
            TlsCrypto crypto,
            string targetHost,
            string[] alpnProtocols,
            int handshakeTimeoutMillis)
            : base(crypto)
        {
            if (handshakeTimeoutMillis <= 0)
                throw new ArgumentOutOfRangeException(nameof(handshakeTimeoutMillis));

            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
            _alpnProtocols = alpnProtocols ?? Array.Empty<string>();
            _handshakeTimeoutMillis = handshakeTimeoutMillis;
        }

        public override TlsAuthentication GetAuthentication()
        {
            // Chain, host identity and signature-algorithm checks (#33); name matching uses the
            // caller's host, normalized by TlsCertificateNameMatcher.
            return new TurboTlsAuthentication(m_context, _targetHost);
        }

        /// <summary>
        /// The handshake bound. BouncyCastle reads it only in blocking mode; the provider drives the
        /// engine non-blocking and enforces this value itself (BouncyCastleTlsProvider.WrapAsync).
        /// </summary>
        public override int GetHandshakeTimeoutMillis() => _handshakeTimeoutMillis;

        /// <summary>
        /// RFC 8446 section 6.1: end of stream without close_notify is a truncation, never a clean
        /// end of data (surfaces as TlsNoCloseNotifyException, an IOException).
        /// </summary>
        public override bool RequiresCloseNotify() => true;

        /// <summary>
        /// No status_request (RFC 6066 section 8): revocation checking is off by decision (#33), so a
        /// staple would go unread.
        /// </summary>
        protected override CertificateStatusRequest GetCertificateStatusRequest() => null;

        /// <summary>No status_request_v2 (RFC 6961), for the same reason.</summary>
        protected override IList<CertificateStatusRequestItemV2> GetMultiCertStatusRequest() => null;

        /// <summary>
        /// RFC 6066 section 3: the A-label form of a DNS host name; no server_name extension for an
        /// IPv4/IPv6 literal.
        /// </summary>
        protected override IList<ServerName> GetSniServerNames()
        {
            var sniHost = TlsHostName.ToSniHostName(_targetHost);
            if (sniHost == null)
                return null;

            return new List<ServerName>
            {
                new ServerName(NameType.host_name, System.Text.Encoding.ASCII.GetBytes(sniHost))
            };
        }

        public override IDictionary<int, byte[]> GetClientExtensions()
        {
            var extensions = base.GetClientExtensions() ?? new Dictionary<int, byte[]>();

            // Add ALPN extension if protocols were specified
            if (_alpnProtocols.Length > 0)
            {
                var protocols = new List<ProtocolName>();
                foreach (var protocol in _alpnProtocols)
                {
                    protocols.Add(ProtocolName.AsUtf8Encoding(protocol));
                }

                TlsExtensionsUtilities.AddAlpnExtensionClient(extensions, protocols);
            }

            // SNI: added by the base implementation from GetSniServerNames().

            return extensions;
        }

        public override void NotifyHandshakeComplete()
        {
            base.NotifyHandshakeComplete();

            // Capture TLS version
            var context = m_context;
            if (context != null)
            {
                var version = context.ServerVersion;
                NegotiatedVersion = FormatTlsVersion(version);

                // Capture negotiated ALPN and cipher suite from SecurityParameters
                var securityParams = context.SecurityParameters;
                if (securityParams != null)
                {
                    var alpnProtocol = securityParams.ApplicationProtocol;
                    if (alpnProtocol != null)
                    {
                        NegotiatedAlpn = alpnProtocol.GetUtf8Decoding();
                    }

                    NegotiatedCipherSuite = FormatCipherSuite(securityParams.CipherSuite);
                }
            }
        }

        private string FormatTlsVersion(ProtocolVersion version)
        {
            if (version == null)
                return "Unknown";

            if (version.Equals(ProtocolVersion.TLSv12))
                return "1.2";
            if (version.Equals(ProtocolVersion.TLSv13))
                return "1.3";

            return version.ToString();
        }

        private string FormatCipherSuite(int cipherSuite)
        {
            return $"0x{cipherSuite:X4}";
        }

        protected override ProtocolVersion[] GetSupportedVersions()
        {
            // Support TLS 1.2 and 1.3 only
            return new[]
            {
                ProtocolVersion.TLSv13,
                ProtocolVersion.TLSv12
            };
        }

        protected override int[] GetSupportedCipherSuites()
        {
            // Explicit list, filtered by what the crypto provider implements.
            return TlsUtilities.GetSupportedCipherSuites(Crypto, OfferedCipherSuites);
        }
    }
}

