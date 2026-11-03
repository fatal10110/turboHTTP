# BouncyCastle provider: duplex non-blocking stream, alert classification (#36, #169, #164)

**Date:** 2026-10-30
**Lane:** bc-provider (wave w1030), milestone v1.0-beta

## What

- **#36 (reads serialise writes).** `BouncyCastleTlsProvider.WrapAsync` returned BouncyCastle's
  `TlsStream`. Under Unity's scripting defines (no `NETSTANDARD2_1_OR_GREATER`) that class compiles
  no async overrides, so every `ReadAsync`/`WriteAsync` fell back to the BCL APM path, which runs
  reads and writes one at a time on a single semaphore. A pending read (always the case for the
  HTTP/2 read loop, and for the HTTP/1.1 `Expect: 100-continue` timeout branch) blocked every write
  until the server sent more data.
- **#169 (sync-over-async handshake).** The blocking `TlsClientProtocol(Stream)` handshake ran on a
  pool thread (at most 4 at a time) and called the inner stream's sync `Read`/`Write`. With
  `SocketIoMode.Saea`/`PollSelect` those calls are `GetAwaiter().GetResult()` over a completion
  that itself needs a pool thread. On a clamped pool this deadlocked.
- **#164 (alerts misclassified).** Only a locally raised `TlsFatalAlert` became
  `AuthenticationException`. A server-sent `TlsFatalAlertReceived` (an `IOException`) mapped to a
  retryable `NetworkError`, so `RetryInterceptor` repeated handshakes that could never succeed.

## Fix

- New `Runtime/Transport/BouncyCastle/BouncyCastleTlsStream.cs` drives `TlsClientProtocol` in
  **non-blocking mode** (`new TlsClientProtocol()`, `OfferInput`/`ReadInput`/`ReadOutput`). It does all
  socket I/O itself through the inner stream's async API:
  - `_protocolLock` guards every engine call. These calls are CPU-only and never await.
  - `_writeGate` (a `SemaphoreSlim(1)`) keeps ciphertext records in order on the socket.
  - The read path has a single owner and never waits for the gate. Records it produces (alerts,
    key updates, the close_notify reply) are drained by whoever holds the gate, using the
    "release, recheck, `Wait(0)`" pattern, so a stalled write can never block a read.
  - Ciphertext drains are not cancelable. A chunk taken from the engine and then dropped would
    desynchronise the record stream. Disposal breaks a stalled socket.
- The handshake (`HandshakeAsync`) is async end to end and starts with `Task.Run` (key-share
  generation is CPU work). No thread ever blocks on the inner stream, whatever `SocketIoMode` is.
  `HandshakeSemaphore(4)` was removed: it only existed to bound pool threads parked by blocking
  handshakes.
- `BouncyCastleTlsProvider.MapHandshakeFailure` classifies `TlsFatalAlertReceived` and `TlsFatalAlert`
  by `AlertDescription` (`IsNonTransientAlert`):
  - Certificate, negotiation and protocol verdicts (RFC 8446 §6.2, RFC 5246 §7.2.2) become
    `AuthenticationException`, which maps to `CertificateError` (not retryable).
  - `internal_error`, `user_canceled`, record-layer alerts and unknown codes become `IOException`
    (`NetworkError`, retryable).
  - The message names the alert and its direction, for example
    `fatal alert handshake_failure(40) received from the server`.

## Files

- `Runtime/Transport/BouncyCastle/BouncyCastleTlsStream.cs` (+ `.meta`), new.
- `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs`: async handshake, classification,
  semaphore removed, remarks rewritten.
- Tests (new, `Tests/Runtime/Transport/Tls/`):
  - `BouncyCastleLoopbackServer.cs`: a dedicated-thread loopback server, either TLS 1.2 `SslStream`
    or raw scripted bytes.
  - `BouncyCastleDuplexStreamTests.cs` (#36): write completes while a read is pending; 50
    concurrent HTTP/2 requests against a loopback h2 server over BC; the `Expect: 100-continue`
    timeout path through `RawSocketTransport` + BC.
  - `BouncyCastleHandshakeThreadPoolTests.cs` (#169): `ThreadPool.SetMaxThreads` clamped,
    `max(8, 2 x workers)` concurrent handshakes over `SaeaStream`, a spy asserting zero sync
    `Read`/`Write` calls, and a kernel-handle wait so a starved pool cannot hang the test.
  - `BouncyCastleFatalAlertTests.cs` (#164): a scripted server answers the ClientHello with
    handshake_failure, protocol_version, bad_certificate or internal_error. Covers the exception
    type and message, plus `RetryInterceptor` (MaxRetries 2): 1 connection for the non-transient
    alerts, 3 for internal_error.
  - `BouncyCastleOutputBacklogTests.cs` (review r1): a writer is stalled on a test inner stream
    and the server sends close_notify. The client engine queues its close_notify reply, and with
    the bound lowered to 1 byte (set by reflection, because the test assembly does not reference
    the BC assembly) the read must fail with `IOException`. The test also checks that the inner
    stream is disposed and the stalled write faults. Without the check, the read returns 0.
- `Tests/Runtime/Transport/Tls/LocalTlsTestServer.cs`: `TryCreateLocalhostCertificate` is now
  `internal` so the new server can reuse it.

## Decisions

- **Non-blocking engine instead of a reader thread.** The issue offered "duplex adapter" or "drive
  `TlsClientProtocol` non-blocking". The non-blocking engine fixes #36 and #169 together, with no
  dedicated thread per connection and no blocking call anywhere.
- **EOF during the handshake is a network error.** The blocking engine turned EOF into a local
  `TlsFatalAlert(handshake_failure)` (non-retryable). The new code throws `IOException`, because a
  peer reset is a transport event, not a TLS verdict.
- **Plain per-connection buffers.** Two 18 KB ciphertext buffers per connection are plain arrays,
  not `ArrayPool` rentals. A disposal racing an in-flight inner read could otherwise return a buffer
  to the pool while the socket still writes into it.
- **Dispose sends close_notify best effort and synchronously.** `Stream.Dispose` is synchronous;
  the old `TlsStream.Dispose` did the same. close_notify is skipped when a write holds the gate.
- **Bounded engine output queue (review r1).** The read path cannot send while a writer holds
  `_writeGate` (stalled on a full socket). Records the peer makes the engine queue meanwhile stay
  in memory. Example: a TLS 1.2 hello_request (4 bytes) is answered with a ~31-byte
  no_renegotiation warning, so the queue grows about 7x faster than the peer sends. After every
  flush attempt the reader checks `GetAvailableOutputBytes()` against
  `DefaultMaxPendingOutputBytes` (64 KiB). Above it, the reader aborts the connection (disposing
  the inner stream also fails the stalled write) and throws `IOException`. The worst case is the
  bound plus one read buffer's worth of amplified output. Waiting for the gate in that case was
  rejected: a stalled write would then block the read, which is the #36 defect again.
- **Loopback h2 server.** "50 concurrent requests against a real h2 server" runs a real
  `Http2Connection` over a real BC TLS session against a minimal scripted h2 server on loopback.
  The test rules forbid public hosts outside `ExternalNetwork`.

## Not done / hand-offs

- **bc-csharp roots: owner action pending.** fatal10110/bc-csharp should refresh the roots file in
  its strip. This has **not** been raised there: this session is not allowed to post to bc-csharp
  (an earlier attempt to comment on bc-csharp #29 was refused). The owner needs to open the issue or
  comment. No TurboHTTP code in this lane depends on it.
- **#169 device item.** "Device run on Android IL2CPP recorded in #99" stays open. The PR says
  "Part of #169". #36's device item is already in the #99 matrix.
- **#33 interaction.** The new tests rely on the BC client accepting the self-signed loopback
  certificate. When chain validation (#33) lands, that lane needs a test trust seam for these
  fixtures.

## Verification

- `'./Tools~/ci/check.sh'`: pass (invariants, format, shadow build with analyzers).
- Unity EditMode/PlayMode were **not run** locally: Unity cannot be driven from this host. The
  Unity tests run on PR CI.
- Extra local evidence: the new test files plus `BouncyCastleProviderTests` were compiled into a
  scratch net8.0 NUnit project against the shadow-build assemblies and run there.
  - New code: 19/19 pass (18 plus the review-r1 backlog test).
  - With the backlog check removed, the backlog test fails ("it returned 0").
  - With `origin/main`'s provider swapped in: every new #36/#169/#164 regression test fails (the
    three #36 tests time out, the 6 alert tests get `TlsFatalAlertReceived` or a retried
    `NetworkError`, and the SAEA test stalls). The internal_error guards pass in both runs.
  - That runtime is CoreCLR, not Unity Mono.
- Not run: IL2CPP, iOS, Android (`needs-device-validation`; #99).
