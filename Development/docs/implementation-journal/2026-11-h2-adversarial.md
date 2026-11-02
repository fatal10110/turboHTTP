# 2026-11 HTTP/2 scripted adversarial server and interop job (#111)

## What

- **Scripted adversarial server.** `Tests/Runtime/Transport/Http2/Adversarial/` contains
  `ScriptedHttp2Server` and `Http2AdversarialScenarios`.
  - The server is a frame-level, single-threaded HTTP/2 server. It drives a real `Http2Connection`
    over the existing in-memory `TestDuplexStream`.
  - Each scenario owns every write. Client frames are read only inside `WaitForAsync`, which folds
    each frame into observed state:
    - It decodes request header blocks in order with its own `HpackDecoder`.
    - It checks that client stream ids are odd and increasing.
    - It checks client DATA against the stream and connection windows the server advertised. An
      `INITIAL_WINDOW_SIZE` change applies when the client ACKs it.
    - It records RST_STREAM, GOAWAY, PING ACK and SETTINGS ACK.
  - `HpackBlock` writes raw HPACK representations: exact insertions, references, and size updates
    anywhere in a block. `SyncAsync` is a PING round trip. After it returns, every client frame
    written before the client read the PING has been observed.
  - Fifteen scenarios cover the #111 list a-g, the gaps the lane brief named, and three extras:
    - a. Header block on a cancelled stream, split over CONTINUATION; the next response resolves
      the entry it inserted.
    - b. CONTINUATION flood (129 frames) and a header block over 64 KB, both answered with
      ENHANCE_YOUR_CALM.
    - c. Connection-level HPACK bomb: 101 bytes that decode to about 400 KB, answered with
      COMPRESSION_ERROR. Table-size churn across six responses: two updates in one block,
      shrink and evict, clear by an oversize insert, regrow. A size update after a field, and one
      above SETTINGS_HEADER_TABLE_SIZE, both answered with COMPRESSION_ERROR.
    - d. GOAWAY(2^31-1), then the real last id, then a raised id, with in-flight POSTs: the
      streams at or below the last id drain; the rest, and new requests, fail as not processed.
      REFUSED_STREAM on the first two streams of a burst.
    - e. INITIAL_WINDOW_SIZE lowered mid-upload: the stream window goes to -49151, the client
      sends nothing at window 0, then sends exactly the new credit.
    - f. 120 requests against MAX_CONCURRENT_STREAMS 100, lowered to 10 while 100 are open.
      Seeded random cancellation (seed 20261103) happens while queued, before the response and
      mid-response. Responses go out in shuffled order through an `HpackEncoder` that indexes
      heavily, including late blocks on reset streams.
    - g. content-length shorter and longer than the DATA.
    - Extra: DATA on reset streams returns connection credit (20 x 60000 bytes, more than the whole
      connection window).
    - Extra: PING and SETTINGS floods (500 each) are all acknowledged.
- **NUnit.** `Tests/Runtime/Transport/Http2/Http2AdversarialServerTests.cs` runs every scenario as a
  `[TestCaseSource]` case, for the host-editor run.
- **CI harness.** `Tools~/h2-interop/` is a plain .NET 8 harness, `TurboHTTP.H2Interop`, built
  against the shadow DLLs like tls-interop. It compiles the `Adversarial/` folder,
  `TestDuplexStream.cs` and tls-interop's `TestPki.cs` through `<Compile Include>`, so PR CI runs the
  same scenario code the NUnit suite runs. The two kinds of rows:
  - Adversarial rows run in memory.
  - Interop rows use the production `RawSocketTransport` (SslStream, ALPN h2) against three
    servers over TLS on loopback: Kestrel (in-process, ASP.NET Core 8), nghttpd (Debian bookworm
    `nghttp2-server` on a digest-pinned base) and nginx 1.27.3 (digest-pinned). The rows are
    `get-small`, `get-large` (2 MiB), `concurrent-100`, `post-upload` (1 MiB, echoed by Kestrel and
    nghttpd), `request-headers-large` and `response-headers-many` (Kestrel, nginx). Each row also
    checks `RawSocketTransport.HasHttp2Connection`.
- **CI wiring.** A new `h2-interop` job in `.github/workflows/ci.yml` runs `run.sh`. The aggregate
  `ci` check needs it.
- `Runtime/Transport/AssemblyInfo.cs` grants `InternalsVisibleTo("TurboHTTP.H2Interop")`. The
  harness needs `Http2Connection`, `Http2FrameCodec` and HPACK. It is not shipped.

## Files

- `Tests/Runtime/Transport/Http2/Adversarial/ScriptedHttp2Server.cs`,
  `Http2AdversarialScenarios.cs` (+ folder and file `.meta`)
- `Tests/Runtime/Transport/Http2/Http2AdversarialServerTests.cs` (+ `.meta`)
- `Tools~/h2-interop/` (csproj, `Directory.Build.props`, `Program.cs`, `InteropRows.cs`,
  `KestrelHost.cs`, `run.sh`, `README.md`, `docker/nghttpd/Dockerfile`, `docker/nginx/*.conf`)
- `.github/workflows/ci.yml`, `Runtime/Transport/AssemblyInfo.cs`

## Decisions

- **Shared source instead of a second copy.** The scenarios use no NUnit: they throw
  `Http2ScenarioException` through `ScenarioAssert`. The NUnit fixture is a thin wrapper. This is how
  the Unity-only suite also runs in PR CI, where Unity tests do not run (#188).
- **h2spec is not used.** It tests servers. The adversarial rows are the client-side counterpart:
  how the client reacts to hostile server frames. #111 scope item 3 ("h2spec-style") is covered this
  way.
- **Interop servers.** The job runs three servers, chosen for CI time and pull cost:
  - Kestrel needs no image.
  - nghttpd and nginx are small images, pinned by digest. nghttpd is installed from a Debian
    package on a pinned base.
  - h2o, Go net/http, Envoy and nghttpx are follow-up #288 (v1.0 GA).
- **IVT.** The harness gets an IVT instead of being limited to the public API, because raw frames
  need `Http2FrameCodec` and HPACK internals.
- **Scenario f strictness.** A PING round trip follows every response, so the client always knows
  every closure except the newest. Its open-stream count is therefore equal to the server's view or
  one higher, so the limit check cannot fail a correct client. Cancellations count as closures the
  moment the scenario cancels.

## Defects found

None. Every scenario and interop row passes on `origin/main` 5fc7659, as #111 expected after the
v1.0-beta HTTP/2 fixes (#210, #224, #234, #241, #244, #252, #254). Sensitivity was checked by
temporarily mutating Transport (not committed):
- Dropping `MaybeSendConnectionWindowUpdateAsync` for post-reset DATA fails
  `flow/reset-stream-data-returns-connection-credit`.
- Flooring the admission limit at 12 fails `streams/max-concurrent-lowered-random-cancel`.

## Verification

- `Tools~/h2-interop/run.sh`: adversarial 15/15, interop Kestrel 6/6, nghttpd 5/5, nginx 6/6. The
  adversarial rows passed three runs in a row.
- `Tools~/ci/check.sh`: OK.
- Unity PlayMode, scoped `--filter Http2AdversarialServerTests`: 15/15 passed (the runtime test assembly runs under PlayMode; an EditMode run discovers 0 tests).
- Not run: the full Unity suites (host-editor periodic run), EditMode (nothing to discover), IL2CPP, devices. The scenarios
  are in-memory, so device behavior (ALPN on iOS/Android) is not covered.

## Deferred

- #288: h2o, Go net/http, Envoy and nghttpx interop rows.
