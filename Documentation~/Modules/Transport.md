# Transport Module

The `Transport` module is the low-level engine of TurboHTTP. It bypasses `UnityWebRequest` entirely, relying on raw managed sockets and a custom HTTP protocol implementation.

## Default Transport

By default, `UHttpClient` uses the `RawSocketTransport`. It handles:
- **HTTP/1.1**: Keep-alive connection pooling, chunked transfer encoding.
- **HTTP/2**: Multiplexed streams over a single connection, HPACK header compression.
- **TLS/SSL**: Uses BouncyCastle by default for broad platform compatibility (including Unity WebGL where applicable, though Native WebSockets/Fetch is used in actual WebGL builds).
- **DNS**: Happy Eyeballs (RFC 8305) for fast IPv4/IPv6 fallback.

### HTTP/2 Multiplexing

HTTP/2 is automatically negotiated via ALPN during the TLS handshake. When active, multiple requests to the same origin share a single TCP connection concurrently.

```csharp
// No extra configuration is needed. It happens automatically.
// You can force HTTP/2 only if needed:
var options = new UHttpClientOptions();
options.Transport = new RawSocketTransport(new TransportSettings {
    EnableHttp2 = true, // Default is true
    ForceHttp2 = false
});
```

## Zero-Allocation Pipeline

The transport layer is heavily optimized for Unity:
- **SocketAwaitable / ValueTask**: Sockets are awaited without allocating standard `Task` objects.
- **MemoryPool<byte>**: Buffers are leased and returned to the shared memory pool.
- **Header Parsing**: HTTP/1.1 and HTTP/2 headers are parsed directly from read spans, avoiding multiple strings. 

## Custom Transports

You can implement `IHttpTransport` to override how requests hit the network. This is how the `Testing` module provides `MockTransport` and `RecordReplayTransport`.

```csharp
public class MyCustomTransport : IHttpTransport
{
    public ValueTask<UHttpResponse> SendAsync(UHttpRequest request, CancellationToken ct)
    {
        // Custom logic
    }
    
    public void Dispose() {}
}

options.Transport = new MyCustomTransport();
var client = new UHttpClient(options);
```
