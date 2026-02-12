

using System;
using System.Collections.Generic;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// BouncyCastle TLS client implementation with ALPN support for HTTP/2.
    /// </summary>
    internal sealed class TurboTlsClient : DefaultTlsClient
    {
        private readonly string _targetHost;
        private readonly string[] _alpnProtocols;

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
            : base(crypto)
        {
            _targetHost = targetHost ?? throw new ArgumentNullException(nameof(targetHost));
            _alpnProtocols = alpnProtocols ?? Array.Empty<string>();
        }

        public override TlsAuthentication GetAuthentication()
        {
            return new TurboTlsAuthentication(_targetHost);
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

            // Add SNI (Server Name Indication) extension
            var hostBytes = System.Text.Encoding.UTF8.GetBytes(_targetHost);
            var serverNames = new List<ServerName>
            {
                new ServerName(NameType.host_name, hostBytes)
            };
            TlsExtensionsUtilities.AddServerNameExtensionClient(extensions, serverNames);

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
            return base.GetSupportedCipherSuites();
        }
    }
}

