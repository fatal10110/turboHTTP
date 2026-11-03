# Step 3C.9: Update TcpConnectionPool

**File:** `Runtime/Transport/Tcp/TcpConnectionPool.cs` (MODIFY)  
**Depends on:** Steps 3C.1, 3C.2, 3C.8  
**Spec:** Integration with ITlsProvider

## Purpose

Replace the direct usage of `TlsStreamWrapper` with the new `ITlsProvider` abstraction. This allows the connection pool to use either SslStream or BouncyCastle transparently based on configuration.

## Changes Required

### 1. Remove `TlsStreamWrapper` Dependency

**Old code:**
```csharp
using TurboHTTP.Transport.Tls.TlsStreamWrapper;
```

**New code:**
```csharp
using TurboHTTP.Transport.Tls;
```

### 2. Update TLS Handshake Logic

**Location:** Inside the method that establishes TCP connections and wraps with TLS.

**Old code** (approximate):
```csharp
// Wrap with TLS
var sslStream = new SslStream(tcpStream, false, ValidateCertificate);
await sslStream.AuthenticateAsClientAsync(host);

// ... continue with HTTP ...
```

**New code:**
```csharp
// Determine TLS provider based on configuration
var tlsProvider = TlsProviderSelector.GetProvider(options.TlsBackend);

// Wrap with TLS
var tlsResult = await tlsProvider.WrapAsync(
    tcpStream,
    host,
    alpnProtocols: new[] { "h2", "http/1.1" },
    cancellationToken)
    .ConfigureAwait(false);  // Don't capture SynchronizationContext

// Store TLS result for protocol routing
var secureStream = tlsResult.SecureStream;
var negotiatedProtocol = tlsResult.NegotiatedAlpn;

// ... continue with HTTP/2 or HTTP/1.1 based on negotiatedProtocol ...
```

### 3. Add Configuration Parameter

**Modify constructor** to accept `TlsBackend` option:

```csharp
public class TcpConnectionPool
{
    private readonly TlsBackend _tlsBackend;

    public TcpConnectionPool(/* existing params */, TlsBackend tlsBackend = TlsBackend.Auto)
    {
        // ... existing initialization ...
        _tlsBackend = tlsBackend;
    }

    // Use _tlsBackend when calling TlsProviderSelector.GetProvider()
}
```

### 4. Pass ALPN Protocols

When calling `WrapAsync()`, provide ALPN protocols in order of preference:

```csharp
string[] alpnProtocols;
if (supportsHttp2)
{
    alpnProtocols = new[] { "h2", "http/1.1" };
}
else
{
    alpnProtocols = new[] { "http/1.1" };
}

var tlsResult = await tlsProvider.WrapAsync(
    tcpStream,
    host,
    alpnProtocols,
    cancellationToken);
```

### 5. Handle ALPN Result

Use `tlsResult.NegotiatedAlpn` to determine protocol:

```csharp
if (tlsResult.NegotiatedAlpn == "h2")
{
    // Use HTTP/2 connection
    return new Http2Connection(tlsResult.SecureStream, /* ... */);
}
else
{
    // Use HTTP/1.1 connection (or null means no ALPN, assume HTTP/1.1)
    return new Http11Connection(tlsResult.SecureStream, /* ... */);
}
```

### 6. Logging (Optional)

Log TLS provider and negotiation result for diagnostics:

```csharp
Debug.Log($"TLS handshake completed: Provider={tlsResult.ProviderName}, " +
          $"Version={tlsResult.TlsVersion}, ALPN={tlsResult.NegotiatedAlpn ?? "none"}");
```

## Full Example

**Updated method** (conceptual):

```csharp
private async Task<IConnection> ConnectAsync(
    string host,
    int port,
    bool useTls,
    CancellationToken ct)
{
    // 1. Establish TCP connection
    var tcpClient = new TcpClient();
    await tcpClient.ConnectAsync(host, port);
    var tcpStream = tcpClient.GetStream();

    Stream stream = tcpStream;

    if (useTls)
    {
        // 2. Select TLS provider
        var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);

        // 3. Perform TLS handshake with ALPN
        var tlsResult = await tlsProvider.WrapAsync(
            tcpStream,
            host,
            alpnProtocols: new[] { "h2", "http/1.1" },
            ct);

        stream = tlsResult.SecureStream;

        // 4. Route based on ALPN result
        if (tlsResult.NegotiatedAlpn == "h2")
        {
            return new Http2Connection(stream, /* ... */);
        }
    }

    // 5. Fallback to HTTP/1.1
    return new Http11Connection(stream, /* ... */);
}
```

## Migration Notes

### Before This Step

- Direct usage of `SslStream` with reflection-based ALPN

### After This Step

- `ITlsProvider` abstraction with pluggable backends
- Automatic provider selection via `TlsProviderSelector`
- Protocol routing based on `TlsResult.NegotiatedAlpn`

## Breaking Changes

**None** for external API. This is an internal refactor. Public API (`HttpClient`, `HttpClientOptions`) remains unchanged (except for new `TlsBackend` option).

## Validation Criteria

- [ ] Code compiles without errors
- [ ] TLS handshake works with SslStream provider
- [ ] TLS handshake works with BouncyCastle provider
- [ ] ALPN negotiation correctly selects HTTP/2 when supported
- [ ] Falls back to HTTP/1.1 when ALPN is null
- [ ] `TlsBackend` configuration option works

## Testing Notes

Test matrix:
1. **Auto mode on Windows**: Should use SslStream
2. **Auto mode on iOS**: Should use BouncyCastle
3. **Forced SslStream**: Should use SslStream regardless of platform
4. **Forced BouncyCastle**: Should use BouncyCastle regardless of platform

## References

- Step 3C.1: `ITlsProvider` interface
- Step 3C.2: `TlsResult` model
- Step 3C.8: `TlsProviderSelector` logic
