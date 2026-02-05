# Step 3C.5: BouncyCastle TurboTlsClient

**File:** `Runtime/Transport/BouncyCastle/TurboTlsClient.cs`  
**Depends on:** Step 3C.4  
**Spec:** RFC 5246 (TLS 1.2), RFC 8446 (TLS 1.3), RFC 7301 (ALPN)

## Purpose

Implement BouncyCastle's `TlsClient` interface to handle client-side TLS negotiation, including ALPN extension for HTTP/2 protocol selection.

## Type to Implement

### `TurboTlsClient` (class)

```csharp
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
            var serverNames = new List<ServerName>
            {
                new ServerName(NameType.host_name, _targetHost)
            };
            TlsExtensionsUtilities.AddServerNameExtension(extensions, serverNames);

            return extensions;
        }

        public override void NotifyAlpnProtocol(ProtocolName protocolName)
        {
            base.NotifyAlpnProtocol(protocolName);

            if (protocolName != null)
            {
                NegotiatedAlpn = protocolName.GetUtf8Decoding();
            }
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

                // Capture cipher suite
                var securityParams = context.SecurityParameters;
                if (securityParams != null)
                {
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
            // BouncyCastle cipher suite constants are in CipherSuite class
            // For diagnostics, just return the hex value
            // Full name resolution would require a lookup table
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
            // Use secure defaults from DefaultTlsClient
            // Override if specific cipher suites are required
            return base.GetSupportedCipherSuites();
        }
    }
}
```

## Implementation Details

### Constructor

Takes three parameters:
- **`crypto`**: BouncyCastle crypto provider (passed through to base class)
- **`targetHost`**: Server hostname for SNI and certificate validation
- **`alpnProtocols`**: Array of ALPN protocols in preference order

### `GetClientExtensions()`

Called by BouncyCastle during ClientHello construction. This method:
1. Gets base extensions from `DefaultTlsClient`
2. **Adds ALPN extension** if protocols were provided
3. **Adds SNI extension** with the target hostname

**ALPN Extension Format (RFC 7301):**
```
opaque ProtocolName<1..2^8-1>;
struct {
    ProtocolName protocol_name_list<2..2^16-1>
} ProtocolNameList;
```

### `NotifyAlpnProtocol()`

Called by BouncyCastle after receiving the server's ALPN selection in ServerHello. Captures the negotiated protocol (e.g., "h2", "http/1.1").

If the server doesn't support ALPN or there's no common protocol, this method is **not called**, and `NegotiatedAlpn` remains `null`.

### `NotifyHandshakeComplete()`

Called after the full TLS handshake completes. Captures:
- **TLS version** from `ServerVersion`
- **Cipher suite** from `SecurityParameters`

### Supported TLS Versions

Only TLS 1.2 and 1.3 are allowed. TLS 1.0/1.1 are deprecated and insecure.

### Cipher Suites

Uses BouncyCastle's default secure cipher suites. Can be overridden in `GetSupportedCipherSuites()` if specific ciphers are required.

## Security Considerations

### ✅ Enforced:
- TLS 1.2 minimum (TLS 1.0/1.1 rejected)
- SNI extension (prevents hostname mismatch attacks)
- ALPN extension (prevents downgrade attacks for HTTP/2)

### ⚠️ Deferred to Step 3C.6:
- Server certificate validation
- Certificate pinning

## Namespace

`TurboHTTP.Transport.BouncyCastle`

## Validation Criteria

- [ ] Class compiles without errors
- [ ] ALPN extension is added to ClientHello when protocols are specified
- [ ] SNI extension is added with correct hostname
- [ ] `NotifyAlpnProtocol()` correctly captures server's ALPN selection
- [ ] TLS version is captured correctly
- [ ] Only TLS 1.2 and 1.3 are supported

## Testing Notes

Test with servers that:
1. **Support ALPN**: `https://www.google.com` (supports "h2")
2. **Don't support ALPN**: Any HTTP/1.1-only server
3. **Support TLS 1.3**: Most modern servers
4. **Only support TLS 1.2**: Some older servers

## References

- [RFC 7301 - TLS ALPN Extension](https://tools.ietf.org/html/rfc7301)
- [RFC 6066 - TLS Extensions (SNI)](https://tools.ietf.org/html/rfc6066#section-3)
- [BouncyCastle TLS API Docs](https://github.com/bcgit/bc-csharp/tree/master/crypto/src/tls)
