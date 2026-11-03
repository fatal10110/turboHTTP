# 2026-11 soak and lossy-network harness (#114)

Wave run w1102, lane soak. One PR. Part of #114: the device run of a shortened soak stays with the
device matrix (#99, owner hardware).

## What

- **Harness** `Tools~/soak/`: a net8.0 console app (`TurboHTTP.Soak.csproj`) that links the shadow-build
  DLLs, like `Tools~/autobahn` and `Tools~/fuzz`. `run.sh` builds it, writes a per-run PKI and runs
  `run`, which starts `serve` as a second process:
  - `SoakServer.cs`: Kestrel (ASP.NET Core 8 shared framework) as the independent peer. It serves
    HTTP/1.1 cleartext with WebSocket echo, and HTTP/1.1 + HTTP/2 over TLS with ALPN, with the RFC 9113
    default 65,535-byte windows. It also hosts the fault proxies and answers `stats` on stdin.
  - `FaultProxy.cs`: a user-space TCP forwarder per profile and server port. Profiles: `latency`,
    `bandwidth`, `lossy` (chunk stalls) and `reset` (RST after a byte budget). `clean` goes direct.
  - `SoakClient.cs` + `Ops.cs`: 8 seeded workers drive one `UHttpClient` over `RawSocketTransport`
    plus `WebSocketClient`s. The mix covers buffered, echo, streamed, large and burst (16 multiplexed)
    requests on HTTP/1.1 and HTTP/2, cancellation before the response and in mid-body, bodies abandoned
    mid-stream, and WebSocket sessions that close normally or are dropped. Every byte is verified, and the
    TLS rows check `x-soak-protocol: HTTP/2`.
  - Checkpoints run after the warm-up, every 600 s and at the end. They pause the workers until nothing
    is in flight, force a full GC, and record the heap, the client's sockets (`/proc/self/fd`), the
    server's open connections and the outstanding pooled objects. The verdict covers growth, failures,
    hangs, unobserved task exceptions, outstanding pool items and sockets still open after dispose
    (README.md).
- **Workflow** `.github/workflows/soak.yml`: `workflow_dispatch` only, `duration`/`profiles` inputs,
  uploads `result.json`, `samples.csv` and `harness.log`. `ci.yml` is untouched.
- **Leak oracle** `Runtime/Core/Internal/ObjectPool.cs`: a return that finds the pool full now counts as
  `DiscardCount`, and `ActiveCount = Rent - Return - Discard`. Before this, every full-pool discard
  stayed "active" forever, so `ActiveCount` drifted up whenever more than `capacity` items came back
  together (above 128 concurrent h2 streams, above 16 h1 parses) and could not serve as a leak check.
  Internal `GetDiagnostics()` accessors were added on `Http2StreamPool`, `ParsedResponsePool` and
  `HeaderParseScratchPool`.
- **IVT** `TurboHTTP.Soak` in `Runtime/Core/AssemblyInfo.cs` and `Runtime/Transport/AssemblyInfo.cs`
  (pool counters only). It is a Tools~ harness, not an asmdef reference, so no module rule changes.

### Defects the soak found (fixed here)

- **Unobserved faulted trailers tasks.** `Http2ResponseBodySource` faulted `_trailersSource` with
  `ObjectDisposedException` whenever a streamed body was disposed before its end, and with the error on
  `Fault`. `Http11ResponseBodySource` did the same for chunked bodies (dispose, read error). Almost
  nobody awaits trailers, so each one raised `TaskScheduler.UnobservedTaskException` when collected.
  The first 60 s run counted 31 of them, and Unity logs these as errors. Both sources now mark the
  exception observed right after `TrySetException` (`_ = task.Exception`). `GetTrailersAsync` still
  throws it.
- **`FailAllStreams` threw `ObjectDisposedException`** (`Http2Connection.Lifecycle.cs`). On the
  `reset` profile, a body reader's WINDOW_UPDATE write could fail after the connection had finished
  disposing. `_cts.Cancel()` on the already-disposed source then threw, and the reader got
  `ObjectDisposedException` instead of the connection's network error. There were 15 to 38 per short
  run. `Dispose` cancels `_cts` before it disposes it, so the late `Cancel` now catches
  `ObjectDisposedException`.
- **`UHttpClient` could not be constructed on plain .NET** with the editor build. The
  `UnityEngine.Application.isPlaying` read sat inside `TryAppendEditorMonitorInterceptor`, so the
  ECall binding failure (`SecurityException`) was thrown before that method's own `try` ran. The read
  now lives in a `NoInlining` helper, and the existing catch sees the failure. Inside Unity nothing
  changes. This affects only the `UNITY_EDITOR` build that the Tools~ harnesses link.

## Tests

- `Tests/Runtime/Performance/ObjectPoolDiscardAccountingTests.cs`: discards are counted, `ActiveCount`
  returns to 0 after returning more items than the capacity, reset-throw keeps the item active, and reset
  clears the counter.
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.PoolLeakUnderStall.cs` (in-memory h2 over
  `TestDuplexStream`):
  - Six stream outcomes under a zero initial window: completed, RST by the peer, abandoned mid-body,
    cancelled waiting for headers, cancelled while blocked on flow control, and credited late. All leave
    the active map empty, and every captured `Http2Stream` goes back to the pool: it is no longer bound
    to the connection and holds no lifetime reference. A PING round trip is the barrier that proves the
    read loop released its leases. Abandoned bodies use `DisposeAsync`, which completes after the
    RST_STREAM write and the release.
  - GOAWAY refuses two streams and drains the third.
  - Abandoned streamed bodies leave no unobserved task exception.
  - `FailAllStreams` after a finished dispose does not throw (reflection on the private method).
- `Tests/Runtime/Performance/SoakShortTests.cs` (HTTP/1.1 over a real loopback server):
  - A fixed-seed mix of 120 operations from 4 workers: buffered, echo, streamed and abandoned. Parser
    pools return to baseline. After dispose the server sees every connection closed.
  - The same mix with cancellation while the server holds the head is `[Explicit]`. On Unity Mono
    (Windows Editor) about one run in 25 never ends a cancelled request, although its socket was
    disposed and the server saw it close: #293 (v1.0-beta). On .NET 8 it always passes.
  - Abandoned chunked bodies leave no unobserved task exception.

## Decisions

- **Pool oracle.** I added the discard counter instead of comparing only at equal load: it makes
  `ActiveCount` exact under any concurrency. The harness asserts `ActiveCount == 0` at quiesced
  checkpoints, where it is the only client in the process. The tests share the process-wide pools
  with other tests:
  - The h1 test compares against a baseline with `<=`.
  - The h2 tests check their own captured streams. With the whole `Http2ConnectionTests` fixture, a
    pool-wide delta picked up a +1 that some earlier test's background work rented during the window.
    That is a lead; I did not chase it.
- **Reset profile.** Every connection is reset after a random 16 KiB to 4 MiB. The first hour (run A)
  reset only a quarter of the connections. The HTTP/2 connection that survived was never reset again:
  3,053 TLS connections were accepted in run B, against 22 in run A.
- **Server-side connection count.** After dispose, only an open proxy connection fails the run (the
  proxy closes as soon as either side does). Kestrel tears its HTTP/2 connections down later: run A
  saw `tlsOpen` 2 and run B saw 4, with the client's own sockets already back to baseline.
- **Byte arrays.** `ArrayPool<byte>.Shared` rents have no counter, so they are checked only through heap
  growth. Follow-up #292 (v1.1).
- **Server.** I used Kestrel rather than a hand-written h2 server: it is an independent, conformant
  peer for h1, h2 (TLS/ALPN, flow control) and WebSocket, and needs no package download (it ships in the
  .NET 8 SDK). It runs in a separate process, so the client's heap and fd counts hold nothing else. I
  did not build an adversarial h2 server; that is #111.
- **Loss.** A user-space proxy cannot drop packets, so loss shows up as chunk stalls (100-600 ms, 2%)
  and resets. This is not a packet-loss validation. `tc netem` needs root and is not used.
- **TLS trust.** The per-run root is trusted only through `SSL_CERT_FILE`/`SSL_CERT_DIR` (the
  tls-interop precedent). There is no accept-all path, and the proxy forwards ciphertext.
- **Errors.** Only the `reset` profile may fail requests, and only with `UHttpException` or
  `WebSocketException` (plus `InvalidOperationException` from a WebSocket send after the peer dropped).
  Any other exception type fails the run, because the error model requires `UHttpException`.
- **Workflow.** `workflow_dispatch` only, like `autobahn.yml`. A weekly hour of CI is the owner's call.

## How verified

Local gate: `'./Tools~/ci/check.sh' --full` passed (see the PR for the pushed head).

**1 h soak, run B** (`'Tools~/soak/run.sh' --duration 1h` at e2f3ef0; WSL2, 4 vCPU, .NET 8.0.33, 8
workers, seed 114, all five profiles): **pass**.

| Checkpoint | t (s) | ops | heap (MiB) | client sockets | pooled items outstanding | resets so far |
| --- | --- | --- | --- | --- | --- | --- |
| baseline | 64.9 | 1,043 | 3.61 | 8 | 0 / 0 / 0 | 26 |
| periodic | 669.4 | 9,414 | 3.80 | 11 | 0 / 0 / 0 | 264 |
| periodic | 1,274.5 | 17,404 | 4.35 | 11 | 0 / 0 / 0 | 487 |
| periodic | 1,875.7 | 24,950 | 3.74 | 9 | 0 / 0 / 0 | 695 |
| periodic | 2,483.5 | 32,428 | 3.94 | 10 | 0 / 0 / 0 | 946 |
| periodic | 3,087.3 | 40,917 | 4.16 | 9 | 0 / 0 / 0 | 1,206 |
| final | 3,610.3 | 47,651 | 4.75 | 13 | 0 / 0 / 0 | 1,400 |

Pooled items are Http2Stream / ParsedResponse / HeaderParseScratch.

- **Heap growth**: 1,194,048 bytes (1.14 MiB) from baseline to final; the limit is 10 MB.
- **Outcomes**: 47,651 operations, 0 failures, 0 unobserved task exceptions. 37,758 ok, 5,269
  cancelled and 3,192 abandoned (both intended), and 1,432 typed faults on the `reset` profile.
- **Connections**: 3,053 TLS and 8,762 cleartext connections were accepted, and 1,400 were reset.
- **After dispose**: client sockets went from 1 before the run to 1 after dispose. Proxy connections
  open: 0. Kestrel `tlsOpen` was 4, which is teardown lag (Decisions).
- Between samples (forced GC only at checkpoints) the heap stayed between 8 and 17 MiB.

**Run A** (the same hour before the `FailAllStreams` fix, with the old quarter-reset profile): heap 3.70 to
4.47 MiB (+0.77), 48,383 operations, 0 failures, pools 0 at all 7 checkpoints, client sockets 1 to 1.
It failed only on the Kestrel-side `tlsOpen` 2, which led to the decision above. The earlier short runs
found the three defects above.

**Deterministic tests**: Unity 2021.3.45f2 PlayMode, host editor, scoped: `--filter
'SoakShortTests|PoolLeak_|AbandonedStreamedBodies|FailAllStreams_AfterDispose|ObjectPoolDiscardAccountingTests|ObjectPoolTests|PoolHealthReporter|Http2FlowControlTests'`:
36/36 pass.

**Fail-before evidence**: the test files were compiled against `.build/shadow` in a scratch .NET 8
NUnit project. With the fixes reverted, 6 tests fail: three of the four discard tests, both
unobserved-exception tests and the `FailAllStreams` test. With the fixes, 210 of 210 pass
(Http2ConnectionTests, SoakShortTests, ObjectPool).

## Not run

- Unity on IL2CPP and on devices (iOS/Android): the shortened device soak is #99. Heap numbers are from
  .NET 8 (CoreCLR, workstation non-concurrent GC), not from Unity Mono or IL2CPP.
- The soak was not run on Windows or macOS. The socket count is Linux only (`/proc/self/fd`).
- Real packet loss (`tc netem`).
- The full Unity suites (host editor, periodic).

## Status

`Development/docs/agents/project-status.md` still lists "soak test (#114)" as open under phases 6/7.
This lane may not edit that file. The harness and the deterministic tests now exist; the device run
(#99) is what is left.
