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

BouncyCastle is compiled only when `TURBOHTTP_BOUNCYCASTLE` is added to Player Settings > Scripting Define Symbols (for every build target that uses it); the `TurboHTTP.Transport.BouncyCastle` assembly then registers its provider at startup (`BouncyCastleModuleRegistration.EnsureRegistered()` does the same by hand). Without the define, selecting `TlsBackend.BouncyCastle` fails with an `InvalidOperationException` naming the define, the glue is not compiled, and nothing roots the vendored BouncyCastle library, so IL2CPP builds and Mono builds with managed code stripping enabled leave `TurboHTTP.SecureProtocol` out (recorded at stripping High on Windows Mono and on Android IL2CPP; a Mono build with stripping Disabled copies every assembly, so `TurboHTTP.SecureProtocol.dll` ships even without the define). When enabled, it validates the certificate chain and host name with the operating system's verifier and trust store: on Windows (Editor and player) with crypt32's SSL chain policy, on macOS (Editor and player) and iOS with the system SecTrust policy, and on Android with the platform trust manager (the app's network security configuration applies). The verifiers never fetch from the network (no revocation check, no intermediate or root download) and reject the certificate on any failure. **Linux is not supported: there is no platform verifier, so `TlsBackend.BouncyCastle` rejects every server certificate there; use `Auto`/`SslStream`.** **Validation of the macOS, iOS and Android verifiers on real hardware is pending (#33), so treat `BouncyCastle` as unvalidated there.**

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
    AllowPlaintextProxyAuth = true,                        // required to send the credentials: the proxy leg is cleartext
    BypassList = new[] { "localhost", "*.internal.example.com" }
};
```

*   Without `Address`, the standard environment variables are used (`HTTPS_PROXY`/`HTTP_PROXY`, `NO_PROXY`, lower-case spellings too). Set `UseEnvironmentVariables = false` to ignore them.
    Only `http://` proxy URLs are supported: an `https://` (or other) value fails the request with `UHttpException` (`InvalidRequest`), never echoing the value.
*   Bypass rules (`BypassList` and the comma-separated `NO_PROXY`): `example.com` matches that host only; `.example.com` matches `example.com` and every subdomain (curl semantics); `*.example.com` matches subdomains only, not the apex; CIDR rules (`10.0.0.0/8`, `fd00::/8`) match IP-literal hosts only, host names are never resolved for them. Any rule may end in `:port`.
*   `ProxySettings.AllowHttpProxyFallbackForHttps` (default `false`) only matters for environment-variable proxies: an `https://` target reads `HTTPS_PROXY` alone, so without `HTTPS_PROXY` it connects directly; with the flag set it falls back to `HTTP_PROXY`. It has no effect on an explicit `Address`.
*   Proxy credentials are sent only to the proxy, as `Proxy-Authorization`, and only in answer to a 407 that offers `Basic`; because the proxy leg is cleartext, `AllowCleartext` and `ProxySettings.AllowPlaintextProxyAuth` must both allow it. `Basic` (RFC 7617 section 4) puts the password in cleartext on the wire, so without `AllowPlaintextProxyAuth = true` the credentials are not sent. On iOS and Android players also set `AllowCleartext = true`, since the proxy leg is cleartext there too (default `false` on mobile). They never reach the origin server, logs or exceptions. See PlatformNotes, "Cleartext traffic".
*   **HTTP/2 through CONNECT is off by default.** Inside a CONNECT tunnel the TLS handshake offers only ALPN `http/1.1`, so requests through a proxy use HTTP/1.1. Set `UHttpClientOptions.Http2.EnableHttp2ThroughConnectTunnels = true` to offer `h2` then `http/1.1`, like a direct connection: an origin that selects `h2` then gets one shared HTTP/2 connection per origin and proxy. HTTP/2 through a tunnel has passed Editor validation only, not yet on iOS or Android devices (#103), so leave it off for device builds until that validation is recorded. Direct connections are not affected by this option; they offer `h2` whenever the TLS backend can negotiate ALPN, which on Unity means `TlsBackend.BouncyCastle` (see "HTTP/2" below).

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.Http2.EnableHttp2ThroughConnectTunnels = true;   // default false: HTTP/1.1 inside CONNECT tunnels
```

## HTTP/2

HTTP/2 over TLS is selected only through ALPN (RFC 9113 section 3.2), and Unity's `SslStream` cannot send or report ALPN. It therefore needs the BouncyCastle backend: add `TURBOHTTP_BOUNCYCASTLE` to Player Settings > Scripting Define Symbols and set `UHttpClientOptions.TlsBackend = TlsBackend.BouncyCastle`. With `Auto` or `SslStream` every HTTPS connection uses HTTP/1.1, and on Linux there is no HTTP/2 at all because `BouncyCastle` rejects every certificate there (see "TLS backends"). Check `(HttpTransportFactory.Capabilities & TransportCapabilities.Http2) != 0` at run time. When active, concurrent requests to the same origin share a TCP connection. Tune it with `UHttpClientOptions.Http2`, and the connection pool with `UHttpClientOptions.ConnectionPool`:

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

## Supported transports

TurboHTTP 1.0 supports only its own transports as `UHttpClientOptions.Transport` (or as the `HttpTransportFactory` default):

| Transport | Module | Use |
| --- | --- | --- |
| `RawSocketTransport` | `TurboHTTP.Transport` | The default network transport (used when `Transport` is not set). |
| `MockTransport`, `MockHttpServer.CreateTransport()`, `RecordReplayTransport` | `TurboHTTP.Testing` | Tests without the network; see [Testing](Testing.md). |

Third-party `IHttpTransport` implementations are not supported in 1.0. A transport needs contracts that are still internal (reading stream and factory request bodies, request trailers, the transport behavior flags), so a custom transport cannot send `WithStreamBody` or `WithBodyFactory` uploads, and members may be added to `IHttpTransport` in a minor release until custom transports are supported. Public custom transport support is planned (#1237). To count, log, change or short-circuit requests, write an interceptor instead ([Interceptors](Interceptors.md)).
