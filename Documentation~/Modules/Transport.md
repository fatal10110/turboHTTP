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
| `Auto` (default) | Uses `SslStream` and never falls back to another provider: a certificate or handshake failure is reported to the caller as a `UHttpException`. Unity's `SslStream` has no ALPN, so connections use HTTP/1.1. |
| `SslStream` | Forces `System.Net.Security.SslStream`. Unity's `SslStream` (Editor and players) has no ALPN, so there is no HTTP/2: connections use HTTP/1.1, and an `h2`-only offer fails with `PlatformNotSupportedException`. |
| `BouncyCastle` | Opt-in (`TURBOHTTP_BOUNCYCASTLE`). Uses the bundled BouncyCastle TLS stack, which negotiates ALPN everywhere: the backend HTTP/2 needs on Unity. |

The platform validates certificates with `SslStream`; TurboHTTP never accepts a certificate the platform would reject, and no option disables validation. BouncyCastle is a transport fallback, never a way around certificate checks.

BouncyCastle is compiled only when `TURBOHTTP_BOUNCYCASTLE` is added to Player Settings > Scripting Define Symbols (for every build target that uses it); the `TurboHTTP.Transport.BouncyCastle` assembly then registers its provider at startup (`BouncyCastleModuleRegistration.EnsureRegistered()` does the same by hand). Without the define, selecting `TlsBackend.BouncyCastle` fails with an `InvalidOperationException` naming the define, the glue is not compiled, and nothing roots the vendored BouncyCastle library in a player build. When enabled, it validates the certificate chain and host name with the operating system's verifier and trust store: on Windows (Editor and player) with crypt32's SSL chain policy, on macOS (Editor and player) and iOS with the system SecTrust policy, and on Android with the platform trust manager (the app's network security configuration applies). The verifiers never fetch from the network (no revocation check, no intermediate or root download) and reject the certificate on any failure. **Linux is not supported: there is no platform verifier, so `TlsBackend.BouncyCastle` rejects every server certificate there; use `Auto`/`SslStream`.** **Validation of the macOS, iOS and Android verifiers on real hardware is pending (#33), so treat `BouncyCastle` as unvalidated there.**

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
    Only `http://` proxy URLs are supported: an `https://` (or other) value fails the request with `UHttpException` (`InvalidRequest`), never echoing the value.
*   Bypass rules (`BypassList` and the comma-separated `NO_PROXY`): `example.com` matches that host only; `.example.com` matches `example.com` and every subdomain (curl semantics); `*.example.com` matches subdomains only, not the apex; CIDR rules (`10.0.0.0/8`, `fd00::/8`) match IP-literal hosts only, host names are never resolved for them. Any rule may end in `:port`.
*   Proxy credentials are sent only to the proxy, as `Proxy-Authorization`, and only in answer to a 407 that offers `Basic`; because the proxy leg is cleartext, `AllowCleartext` and `ProxySettings.AllowPlaintextProxyAuth` must both allow it. They never reach the origin server, logs or exceptions. See PlatformNotes, "Cleartext traffic".
*   **HTTP/2 through CONNECT is off by default.** Inside a CONNECT tunnel the TLS handshake offers only ALPN `http/1.1`, so requests through a proxy use HTTP/1.1. Set `UHttpClientOptions.Http2.EnableHttp2ThroughConnectTunnels = true` to offer `h2` then `http/1.1`, like a direct connection: an origin that selects `h2` then gets one shared HTTP/2 connection per origin and proxy. HTTP/2 through a tunnel has passed Editor validation only, not yet on iOS or Android devices (#103), so leave it off for device builds until that validation is recorded. Direct connections are not affected by this option; they always offer `h2`.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Http2.EnableHttp2ThroughConnectTunnels = true;   // default false: HTTP/1.1 inside CONNECT tunnels
```

## HTTP/2

HTTP/2 is negotiated via ALPN during the TLS handshake and needs no configuration. When active, concurrent requests to the same origin share a TCP connection. Tune it with `UHttpClientOptions.Http2`, and the connection pool with `UHttpClientOptions.ConnectionPool`:

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Http2.MaxConcurrentStreams = 50;
options.ConnectionPool.MaxConnectionsPerHost = 8;
options.ConnectionPool.ConnectTimeout = TimeSpan.FromSeconds(10);
```

*   **One connection per origin by default.** `Http2.MaxConnectionsPerOrigin` (default 1, as RFC 9113 Section 9.1 recommends) lets a server that advertises a low concurrent-stream limit get more connections: another opens only when every connection of the origin has reached that limit, and the count never exceeds the maximum. At the maximum, requests wait for a free stream.
*   **Optional PING before reusing an idle connection.** With `Http2.ReuseProbeIdleThreshold` above zero (default off), a request about to use a connection that received nothing for that long first sends one PING and waits up to `Http2.PingTimeout`. A connection that does not answer is closed and the request goes out on a fresh one (a body that cannot be replayed fails instead). It is off by default because idle PINGs cost battery and peers may treat frequent PINGs as abuse.

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Http2.MaxConnectionsPerOrigin = 2;                          // default 1
options.Http2.ReuseProbeIdleThreshold = TimeSpan.FromSeconds(60);   // default TimeSpan.Zero (off)
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
