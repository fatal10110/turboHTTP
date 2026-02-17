# Step 3C.1: ITlsProvider Interface

**File:** `Runtime/Transport/Tls/ITlsProvider.cs`  
**Depends on:** Nothing  
**Spec:** RFC 7301 (TLS ALPN Extension)

## Purpose

Define the abstraction layer for TLS providers, allowing the library to seamlessly switch between `SslStream` and BouncyCastle implementations. This interface encapsulates TLS handshake, ALPN negotiation, and certificate validation.

## Interface to Implement

### `ITlsProvider`

```csharp
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
        /// <exception cref="AuthenticationException">
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
```

## Design Notes

### Method Signature

- **`innerStream`**: The raw TCP socket stream. **Provider takes ownership** and wraps it.
  - **On success**: The returned `SecureStream` takes ownership of `innerStream`. Caller must NOT dispose `innerStream`.
  - **On failure**: Provider closes `innerStream` before throwing. Caller must NOT dispose `innerStream`.
  - **Summary**: Once `WrapAsync()` is called, caller should never access or dispose `innerStream` regardless of success/failure.
- **`host`**: Used for:
  - SNI (Server Name Indication) in the TLS ClientHello
  - Certificate validation (verify server cert matches hostname)
- **`alpnProtocols`**: Array in preference order. First match wins. Empty array = ALPN not used.
- **Returns**: `TlsResult` containing the secure stream and negotiation metadata.

### Error Handling

- **`AuthenticationException`**: Certificate validation failed or TLS handshake error.
- **`IOException`**: Network errors (connection dropped, timeout, etc.).
- **`OperationCanceledException`**: User cancelled via `CancellationToken`.

### ALPN Support Detection

The `IsAlpnSupported()` method allows runtime detection:
- **SslStream**: May return `false` on platforms where reflection-based ALPN doesn't work (IL2CPP builds).
- **BouncyCastle**: Always returns `true` (full C# implementation, no platform dependencies).

This enables `TlsProviderSelector` to automatically choose the right provider.

### Provider Name

Used for logging and diagnostics. Helps developers understand which TLS implementation was used for a connection.

## Namespace

`TurboHTTP.Transport.Tls`

## Validation Criteria

- [ ] Interface compiles without errors
- [ ] No Unity engine references (noEngineReferences: true)
- [ ] Interface is public (exported from assembly)
- [ ] XML documentation comments are complete
- [ ] All exception types referenced are in System namespace (portable)

## Implementation Notes

This is only the interface definition. Actual implementations are:
- **Step 3C.3**: `SslStreamTlsProvider`
- **Step 3C.7**: `BouncyCastleTlsProvider`

Both implementations will follow this contract exactly.
