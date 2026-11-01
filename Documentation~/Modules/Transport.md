# Transport Module

The `Transport` module is the low-level engine of TurboHTTP. It bypasses `UnityWebRequest` entirely, relying on raw managed sockets and a custom HTTP protocol implementation.

## Default Transport

By default, `UHttpClient` uses the `RawSocketTransport`. It handles:
- **HTTP/1.1**: Keep-alive connection pooling, chunked transfer encoding.
- **HTTP/2**: Multiplexed streams over a single connection, HPACK header compression.
- **TLS/SSL**: `TlsBackend.Auto` (default) uses `SslStream` and never falls back to another provider: TLS failures are reported to the caller. Without SslStream ALPN support, connections use HTTP/1.1. BouncyCastle is opt-in and used only when `TlsBackend.BouncyCastle` is selected explicitly: add `TURBOHTTP_BOUNCYCASTLE` to Player Settings > Scripting Define Symbols (for every build target that uses it) to compile the `TurboHTTP.Transport.BouncyCastle` assembly, which registers its provider at startup (`BouncyCastleModuleRegistration.EnsureRegistered()` does the same by hand). Without the define, selecting `TlsBackend.BouncyCastle` throws `InvalidOperationException` naming the define, the glue is not compiled, and nothing roots the vendored BouncyCastle library in a player build. When enabled, it validates the certificate chain and host name: in the Editor and on desktop with `X509Chain`, on iOS with the system SecTrust policy and on Android with the platform trust manager (the app's network security configuration applies). The iOS and Android verifiers never fetch from the network and reject the certificate on any failure; device validation is pending.
- **DNS**: Happy Eyeballs (RFC 8305) for fast IPv4/IPv6 fallback.
- **Cleartext policy**: `http://` targets and `http://` proxy legs are refused before any socket is opened (`UHttpErrorType.InvalidRequest`) unless `UHttpClientOptions.AllowCleartext` is `true` (default `false` on iOS/Android players, `true` in the Editor and on desktop) and the platform policy permits the host. Basic proxy credentials are sent only in answer to a 407 that offers `Basic`, and only when `AllowPlaintextProxyAuth` is set: CONNECT retries once (on a new proxy connection when the 407 closes the connection or its body cannot be delimited), and HTTP forward proxying (`http://` targets) resends the request once when its body is replayable, otherwise the 407 is returned. See PlatformNotes, "Cleartext traffic".

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
