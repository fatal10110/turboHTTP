# h2-interop: HTTP/2 adversarial server and interop job (#111)

`run.sh` builds a plain .NET 8 harness against the shadow-built TurboHTTP DLLs and runs two kinds of rows.
The `h2-interop` job in `.github/workflows/ci.yml` runs it on every PR; Unity is not needed.

## Adversarial rows (`--only adversarial`)

Scripted, frame-level HTTP/2 server scenarios from `Tests/Runtime/Transport/Http2/Adversarial/`.
`ScriptedHttp2Server` drives a real `Http2Connection` over an in-memory duplex stream. It decodes
every client header block in order and checks client DATA against the windows it advertised. The
NUnit fixture `Http2AdversarialServerTests` runs the same scenarios in the host-editor Unity run.
The scenarios are deterministic: they use no wall clock and fixed seeds, and the token is only a
watchdog.

| Row | What the client must do |
| --- | --- |
| `hpack-sync/cancelled-stream-header-block` | Decode a split header block on a stream it reset, so the next response resolves the inserted entry (RFC 9113 4.3) |
| `continuation/flood`, `continuation/huge-header-block` | Send GOAWAY(ENHANCE_YOUR_CALM) for more than 128 CONTINUATION frames or a header block over 64 KB |
| `hpack/bomb-indexed-references` | Send GOAWAY(COMPRESSION_ERROR) when 101 bytes reference a 4 KB entry 100 times (RFC 7541 7.3) |
| `hpack/table-size-churn` | Resolve every reference through size updates, eviction, clearing and regrowth |
| `hpack/size-update-after-field`, `hpack/size-update-above-settings` | Send COMPRESSION_ERROR (RFC 7541 4.2, 6.3) |
| `goaway/in-flight-posts-drain` | Under GOAWAY(2^31-1), then the last id, then a raised id: drain streams at or below the last id; fail the others, and new requests, as not processed |
| `goaway/refused-stream-burst` | Fail only the REFUSED_STREAM streams, as not processed (RFC 9113 8.7) |
| `flow/initial-window-decrease-mid-upload` | Send nothing while a lowered INITIAL_WINDOW_SIZE keeps the stream window negative, then exactly the credit (RFC 9113 6.9.2) |
| `flow/reset-stream-data-returns-connection-credit` | Return connection credit for DATA on reset streams |
| `streams/max-concurrent-lowered-random-cancel` | Keep 120 requests within MAX_CONCURRENT_STREAMS 100, then 10, under seeded random cancellation. HPACK stays in sync and stream ids increase |
| `body/content-length-short`, `body/content-length-long` | Fail the request and send RST_STREAM(PROTOCOL_ERROR); the connection stays usable (RFC 9113 8.1.1) |
| `control/ping-and-settings-flood` | Acknowledge 500 PINGs and 500 SETTINGS; the connection stays usable |

## Interop rows (`--only interop`)

The production `RawSocketTransport` (SslStream, ALPN h2) talks to three servers over TLS on loopback:

- Kestrel (ASP.NET Core 8, in-process)
- nghttpd (Debian bookworm `nghttp2-server`, image pinned by digest)
- nginx 1.27.3 (pinned by digest)

The rows are `get-small`, `get-large` (2 MiB), `concurrent-100`, `post-upload` (1 MiB, echoed by
Kestrel and nghttpd), `request-headers-large` and `response-headers-many` (Kestrel and nginx). Every
row checks that the request ran on an HTTP/2 connection. The per-run root
(`Tools~/tls-interop/TestPki.cs`) is trusted only through `SSL_CERT_FILE`/`SSL_CERT_DIR`.

The `bc-*` rows (#255) run the same client with `TlsBackend.BouncyCastle` against each server:

| Row | What it proves |
| --- | --- |
| `bc-get-small` | BouncyCastle offers ALPN h2 and serves one HTTP/2 request directly (no proxy). |
| `bc-connect-get-small` | Through a real CONNECT tunnel (`ConnectProxy.cs`, RFC 9110 9.3.6), with `Http2Options.EnableHttp2ThroughConnectTunnels` on (default off, #103), BouncyCastle negotiates h2 inside the tunnel. One GET runs, then 20 concurrent GETs multiplex on that connection. The HTTP/2 connection is keyed by origin and proxy, no direct connection exists, and the proxy sees exactly one `CONNECT 127.0.0.1:<port>`. |
| `bc-connect-get-large` | 2 MiB through the tunnel: HTTP/2 flow control over BouncyCastle records over the relayed stream. |

A missing BouncyCastle provider fails the whole interop run as a precondition and never skips it (#140).
BouncyCastle has no platform verifier on Linux (#248), so the harness trusts the per-run root through
`ServerCertificateVerifier.AddTestTrustAnchor`, as `Tools~/tls-interop` does. SslStream ignores that
hook. These rows are the Editor-independent proof for #255: the Unity Editor's Mono `SslStream` has no
ALPN, and the vendored BouncyCastle strip has no TLS server, so no loopback origin inside the Editor can
select h2. Device validation of h2 through CONNECT stays with #103.

h2spec tests servers, not clients, so it is not used. The adversarial rows are the client-side
counterpart: they check how the client reacts to hostile server frames.

## Usage

```bash
export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"
'Tools~/h2-interop/run.sh'                                  # everything
'Tools~/h2-interop/run.sh' --only adversarial --row hpack/  # one group, no docker
'Tools~/h2-interop/run.sh' --only interop --no-shadow-build
```

Exit codes:

- 0: every row passed.
- 1: a row or a precondition failed. Nothing is ever skipped.
- 2: usage error.
- 3: dotnet or docker is missing.

Logs are written to `.build/h2-interop/run`.
