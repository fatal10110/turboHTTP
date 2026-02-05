# Step 3C.7: BouncyCastle TLS Provider

**File:** `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs`  
**Depends on:** Steps 3C.1, 3C.2, 3C.4, 3C.5, 3C.6  
**Spec:** RFC 5246 (TLS 1.2), RFC 8446 (TLS 1.3), RFC 7301 (ALPN)

## Purpose

Implement `ITlsProvider` using BouncyCastle's pure C# TLS implementation. This provider works on all platforms, including IL2CPP builds where SslStream ALPN may fail.

## Type to Implement

### `BouncyCastleTlsProvider` (class)

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Security;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls;
using TurboHTTP.SecureProtocol.Org.BouncyCastle.Tls.Crypto.Impl.BC;
using TurboHTTP.Transport.Tls;
using UnityEngine.Scripting;

namespace TurboHTTP.Transport.BouncyCastle
{
    /// <summary>
    /// TLS provider using BouncyCastle pure C# implementation.
    /// Guaranteed to work on all platforms, including IL2CPP/AOT builds.
    /// </summary>
    [Preserve]  // Prevent IL2CPP from stripping (loaded via reflection)
    internal sealed class BouncyCastleTlsProvider : ITlsProvider
    {
        public static readonly BouncyCastleTlsProvider Instance = new();

        public string ProviderName => "BouncyCastle";

        private BouncyCastleTlsProvider()
        {
            // Singleton pattern
        }

        public bool IsAlpnSupported() => true;  // Always supported

        public async Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            if (innerStream == null)
                throw new ArgumentNullException(nameof(innerStream));
            if (string.IsNullOrEmpty(host))
                throw new ArgumentNullException(nameof(host));

            // BouncyCastle handshake is blocking; run on thread pool to avoid blocking Unity main thread
            return await Task.Run(() => PerformHandshake(innerStream, host, alpnProtocols, ct), ct)
                .ConfigureAwait(false);  // Don't capture SynchronizationContext
        }

        private TlsResult PerformHandshake(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            // Check cancellation before expensive operations
            ct.ThrowIfCancellationRequested();
            
            // Create crypto provider
            var secureRandom = new SecureRandom();
            var crypto = new BcTlsCrypto(secureRandom);
            
            ct.ThrowIfCancellationRequested();

            // Create TLS protocol handler
            var protocol = new TlsClientProtocol(innerStream);

            try
            {
                // Create our custom TLS client
                var client = new TurboTlsClient(crypto, host, alpnProtocols ?? Array.Empty<string>());

                // Perform TLS handshake (blocking)
                // This sends ClientHello, processes ServerHello, validates certificates, etc.
                protocol.Connect(client);

                // Handshake completed successfully
                return new TlsResult(
                    secureStream: protocol.Stream,
                    negotiatedAlpn: client.NegotiatedAlpn,
                    tlsVersion: client.NegotiatedVersion ?? "Unknown",
                    cipherSuite: client.NegotiatedCipherSuite,
                    providerName: ProviderName);
            }
            catch (Exception ex)
            {
                // Handshake failed - clean up
                try { protocol.Close(); } catch { }
                
                // Re-throw as standard .NET exception
                if (ex is TlsFatalAlert alert)
                {
                    throw new System.Security.Authentication.AuthenticationException(
                        $"TLS handshake failed: {alert.AlertDescription}", ex);
                }
                else if (ex is IOException)
                {
                    throw;  // Already an IOException
                }
                else
                {
                    throw new IOException("TLS handshake failed", ex);
                }
            }
        }
    }
}
```

## Implementation Details

### Singleton Pattern

`BouncyCastleTlsProvider` is a stateless singleton. All state is per-handshake (local variables).

### Async Wrapper

BouncyCastle's `TlsClientProtocol.Connect()` is **blocking** (synchronous). To avoid blocking Unity's main thread:
1. Wrap the handshake in `Task.Run()`
2. Run on thread pool
3. Check `CancellationToken` before expensive operations

> [!WARNING]
> **CancellationToken Limitation**
> 
> The `CancellationToken` is checked **before** the handshake begins, but once `protocol.Connect()` starts, it **cannot be interrupted**. This is a BouncyCastle API limitation. The handshake will complete or fail on its own (typically 100-500ms).
> 
> If cancellation is critical, consider wrapping with a timeout:
> ```csharp
> var cts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
> cts.CancelAfter(TimeSpan.FromSeconds(30));  // 30s handshake timeout
> ```

### Handshake Flow

1. **Create crypto provider**: `BcTlsCrypto` with secure random number generator
2. **Create protocol handler**: `TlsClientProtocol` wrapping the TCP stream
3. **Create TLS client**: `TurboTlsClient` with ALPN configuration
4. **Connect**: Calls `protocol.Connect(client)`, which:
   - Sends ClientHello (with ALPN and SNI extensions)
   - Receives ServerHello
   - Validates server certificate via `TurboTlsAuthentication`
   - Exchanges keys
   - Completes handshake
5. **Return result**: Wrapped stream + negotiation metadata

### Error Handling

**BouncyCastle exceptions:**
- `TlsFatalAlert`: TLS protocol error (e.g., certificate validation failure, protocol violation)
- `IOException`: Network error (connection dropped, etc.)

**Conversion to .NET standard exceptions:**
- `TlsFatalAlert` → `AuthenticationException` (certificate/handshake failures)
- `IOException` → re-thrown as-is
- Other exceptions → wrapped in `IOException`

This provides a consistent exception contract with `SslStreamTlsProvider`.

### Cleanup on Failure

If handshake fails, `protocol.Close()` is called to clean up resources. Exceptions during cleanup are swallowed (already in error state).

### Thread Safety

Each handshake creates a new `TlsClientProtocol` instance. No shared state. Multiple concurrent handshakes are safe.

## ALPN Support

BouncyCastle **always** supports ALPN:
- No platform dependencies
- No reflection
- Pure C# implementation

`IsAlpnSupported()` always returns `true`.

## Performance Considerations

**BouncyCastle vs SslStream:**
- **SslStream**: Uses platform-native TLS (Schannel on Windows, SecureTransport on macOS, OpenSSL on Linux)
  - Faster (native code, hardware acceleration)
  - Smaller memory footprint
- **BouncyCastle**: Pure C# implementation
  - Slower (~2-3x handshake time)
  - Larger memory footprint
  - **But:** More predictable, works everywhere

**Recommendation:**
- Use SslStream where it works (desktop platforms)
- Use BouncyCastle as fallback (mobile IL2CPP builds)

## IL2CPP Compatibility

✅ **Fully compatible:**
- No reflection (except for standard .NET types within BouncyCastle itself)
- No runtime code generation
- No platform-specific P/Invoke
- Pure C# implementation

## Namespace

`TurboHTTP.Transport.BouncyCastle`

## Validation Criteria

- [ ] Class compiles without errors
- [ ] Handshake completes successfully with valid servers
- [ ] ALPN negotiation returns "h2" for HTTP/2 servers
- [ ] Certificate validation rejects invalid certificates
- [ ] Exceptions are converted to standard .NET types
- [ ] No Unity engine references

## Testing Notes

Test with:
1. **HTTP/2 server**: `https://www.google.com` (should negotiate "h2")
2. **HTTP/1.1 server**: Any server without HTTP/2 support
3. **Invalid certificate**: `https://expired.badssl.com/`
4. **Self-signed certificate**: `https://self-signed.badssl.com/`

## References

- [BouncyCastle TLS API](https://github.com/bcgit/bc-csharp/tree/master/crypto/src/tls)
- [RFC 5246 - TLS 1.2](https://tools.ietf.org/html/rfc5246)
- [RFC 8446 - TLS 1.3](https://tools.ietf.org/html/rfc8446)
- [RFC 7301 - TLS ALPN](https://tools.ietf.org/html/rfc7301)
