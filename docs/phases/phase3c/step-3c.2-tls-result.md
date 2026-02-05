# Step 3C.2: TlsResult Model

**File:** `Runtime/Transport/Tls/TlsResult.cs`  
**Depends on:** Nothing  
**Spec:** RFC 7301 (TLS ALPN), RFC 5246/8446 (TLS 1.2/1.3)

## Purpose

Define the result object returned by `ITlsProvider.WrapAsync()`, containing the TLS-secured stream and metadata about the negotiation (ALPN protocol, TLS version, cipher suite).

## Type to Implement

### `TlsResult` (class)

```csharp
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
```

## Design Notes

### Immutability

This is a **sealed, immutable** class:
- Properties are `{ get; }` only (init in constructor)
- Prevents accidental modification after creation
- Thread-safe by design

### Property Semantics

#### `SecureStream`
- **REQUIRED** (throws if null)
- This is the stream to use for all subsequent I/O
- The original TCP stream should be considered "owned" by the TLS layer and not used directly

#### `NegotiatedAlpn`
- **NULLABLE**
- `null` is semantically different from `""` (empty string):
  - `null` = ALPN not available or no match
  - Non-null = ALPN protocol that was selected
- Consumers use this to decide protocol routing (HTTP/2 vs HTTP/1.1)

#### `TlsVersion`
- **REQUIRED** (throws if null)
- Format: `"1.2"` or `"1.3"` (simple string for ease of logging)
- Used for diagnostics and compliance checks (e.g., enforce TLS 1.2 minimum)

#### `CipherSuite`
- **NULLABLE**
- Not all providers can expose this (platform limitations)
- Primarily for logging and security audits

#### `ProviderName`
- **REQUIRED** (defaults to `"Unknown"` if not provided)
- Useful for debugging "which TLS backend was used?"
- Example log: `"TLS handshake completed via BouncyCastle: TLS 1.3, h2"`

### Constructor Validation

- `secureStream` and `tlsVersion` must not be null (throws `ArgumentNullException`)
- Other parameters are nullable/optional

## Usage Example

```csharp
// Example from BouncyCastleTlsProvider:
return new TlsResult(
    secureStream: protocol.Stream,
    negotiatedAlpn: client.NegotiatedAlpn,  // "h2" or null
    tlsVersion: "1.3",
    cipherSuite: "TLS_AES_128_GCM_SHA256",
    providerName: "BouncyCastle"
);

// Example from SslStreamTlsProvider:
return new TlsResult(
    secureStream: sslStream,
    negotiatedAlpn: ExtractAlpnViaReflection(sslStream),  // may be null
    tlsVersion: sslStream.SslProtocol.ToString(),
    cipherSuite: null,  // SslStream doesn't expose this easily
    providerName: "SslStream"
);
```

## Namespace

`TurboHTTP.Transport.Tls`

## Validation Criteria

- [ ] Class compiles without errors
- [ ] All properties are immutable (no setters)
- [ ] Constructor enforces non-null for required properties
- [ ] No Unity engine references
- [ ] XML documentation is complete

## Implementation Notes

- This is a **simple data model** with no logic
- Thread-safe due to immutability
- Works on all .NET platforms (no reflection, no platform-specific APIs)
