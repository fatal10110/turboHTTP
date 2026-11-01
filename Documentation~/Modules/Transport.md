# Transport Module

The `TurboHTTP.Transport` module is the low-level engine of TurboHTTP. It bypasses `UnityWebRequest` entirely, relying on raw managed sockets and a custom HTTP protocol implementation.

## Default Transport

By default, `UHttpClient` uses the `RawSocketTransport`. It handles:
- **HTTP/1.1**: Keep-alive connection pooling, chunked transfer encoding.
- **HTTP/2**: Multiplexed streams over a single connection, HPACK header compression.
- **TLS/SSL**: see "TLS backends" below.
- **DNS**: Happy Eyeballs (RFC 8305) for fast IPv4/IPv6 fallback.
- **Cleartext policy**: `http://` targets and `http://` proxy legs are refused before any socket is opened (`UHttpErrorType.InvalidRequest`) unless `UHttpClientOptions.AllowCleartext` is `true` (default `false` on iOS/Android players, `true` in the Editor and on desktop) and the platform policy permits the host. See PlatformNotes, "Cleartext traffic".

## TLS backends

`UHttpClientOptions.TlsBackend` selects the TLS provider:

| Value | Behavior |
| :--- | :--- |
| `Auto` (default) | Uses `SslStream` and never falls back to another provider: a certificate or handshake failure is reported to the caller as a `UHttpException`. Without `SslStream` ALPN support, connections use HTTP/1.1. |
| `SslStream` | Forces `System.Net.Security.SslStream`; ALPN (and so HTTP/2) may be unavailable on some platforms. |
| `BouncyCastle` | Opt-in. Uses the bundled BouncyCastle TLS stack, which negotiates ALPN everywhere. |

The platform validates certificates with `SslStream`; TurboHTTP never accepts a certificate the platform would reject, and no option disables validation. BouncyCastle is a transport fallback, never a way around certificate checks.

BouncyCastle is compiled only when `TURBOHTTP_BOUNCYCASTLE` is added to Player Settings > Scripting Define Symbols (for every build target that uses it); the `TurboHTTP.Transport.BouncyCastle` assembly then registers its provider at startup (`BouncyCastleModuleRegistration.EnsureRegistered()` does the same by hand). Without the define, selecting `TlsBackend.BouncyCastle` fails with an `InvalidOperationException` naming the define, the glue is not compiled, and nothing roots the vendored BouncyCastle library in a player build. When enabled, it validates the certificate chain and host name: in the Editor and on desktop with `X509Chain`, on iOS with the system SecTrust policy and on Android with the platform trust manager (the app's network security configuration applies). The iOS and Android verifiers never fetch from the network and reject the certificate on any failure. **Device validation of these verifiers is pending (#33), so treat `BouncyCastle` as unvalidated on iOS and Android.**

```csharp
var options = new UHttpClientOptions { TlsBackend = TlsBackend.BouncyCastle };
```

## Proxies

`UHttpClientOptions.Proxy` configures an HTTP proxy. `https://` targets go through a CONNECT tunnel (RFC 9110 section 9.3.6); `http://` targets are forwarded.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Proxy = new ProxySettings
{
    Address = new Uri("http://proxy.example.com:3128"),   // only http:// proxy addresses are supported
    Credentials = new System.Net.NetworkCredential("user", "password"),
    BypassList = new[] { "localhost", "*.internal.example.com" }
};
```

*   Without `Address`, the standard environment variables are used (`HTTPS_PROXY`/`HTTP_PROXY`, `NO_PROXY`, lower-case spellings too). Set `UseEnvironmentVariables = false` to ignore them.
*   Proxy credentials are sent only to the proxy, as `Proxy-Authorization`, and only in answer to a 407 that offers `Basic`; because the proxy leg is cleartext, `AllowCleartext` and `ProxySettings.AllowPlaintextProxyAuth` must both allow it. They never reach the origin server, logs or exceptions. See PlatformNotes, "Cleartext traffic".
*   **HTTP/2 through CONNECT.** A tunnel offers ALPN `h2` and `http/1.1` like a direct connection. This path passed Editor validation only; it has not been validated on iOS or Android devices (#103), so do not rely on HTTP/2 inside a CONNECT tunnel on devices yet. There is no switch for it in v1.0.

## HTTP/2

HTTP/2 is negotiated via ALPN during the TLS handshake and needs no configuration. When active, concurrent requests to the same origin share a TCP connection. Tune it with `UHttpClientOptions.Http2`, and the connection pool with `UHttpClientOptions.ConnectionPool`:

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Http2.MaxConcurrentStreams = 50;
options.ConnectionPool.MaxConnectionsPerHost = 8;
options.ConnectionPool.ConnectTimeout = TimeSpan.FromSeconds(10);
```

## Allocation-conscious pipeline

- **ValueTask:** hot paths avoid allocating `Task` objects.
- **Pooled buffers:** buffers are leased from the shared pool and returned on every path.
- **Header parsing:** HTTP/1.1 and HTTP/2 headers are parsed directly from read spans.

Every peer-controlled size (header bytes and lines, frames, CONTINUATION blocks, decompressed bodies, queues) is bounded.

## Custom transports

Implement `IHttpTransport` to override how requests hit the network. This is how the `Testing` module provides `MockTransport` and `RecordReplayTransport`. A transport reports the response to the `IHttpHandler` it is given.

```csharp
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport;

// Counts requests and delegates to another transport.
public sealed class CountingTransport : IHttpTransport
{
    private readonly IHttpTransport _inner;
    private int _count;

    public CountingTransport(IHttpTransport inner) => _inner = inner;

    public int Count => Volatile.Read(ref _count);

    public Task DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return _inner.DispatchAsync(request, handler, context, cancellationToken);
    }

    public void Dispose() => _inner.Dispose();
}

public static class CustomTransportExample
{
    public static UHttpClient Create()
    {
        var options = new UHttpClientOptions
        {
            Transport = new CountingTransport(new RawSocketTransport()),
            DisposeTransport = true // the client disposes the transport with itself
        };
        return new UHttpClient(options);
    }
}
```
