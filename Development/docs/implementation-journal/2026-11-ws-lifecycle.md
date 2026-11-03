# 2026-11 ws-lifecycle (#83, #84, #86)

Wave run w1102, lane ws-lifecycle. One PR, all in `TurboHTTP.WebSocket`. No new asmdef references.

## What

- **#83: a non-101 handshake hangs and loses the status; the resilient client retries auth failures.**
  - `WebSocketHandshakeValidator.ReadErrorBodyAsync` now reads the error body with the framing its
    headers declare (RFC 9112 section 6.3):
    - 1xx, 204 and 304 have no body.
    - With Transfer-Encoding, the body is chunked when chunked is the final coding (section 7.1:
      chunk-ext ignored, 1 KB line limit, trailers not read). Otherwise it is close-delimited.
    - A valid Content-Length (a list of identical values counts as one) reads exactly that many bytes.
      An invalid one skips the body.
    - With neither header, the body is close-delimited.
    - Reading stops at `maxErrorBodyBytes` (4 KB) in every case.
    - Any failure after the status line (reset, malformed chunk, cancellation) keeps what was read.
      The status decides the outcome.
    - A malformed response head keeps only the bytes already read and does no further read.
  - `WebSocketConnection.ConnectAsync` maps `WebSocketHandshakeException` before the timeout check.
    A response that arrived is therefore reported as `HandshakeFailed` with its status even if the
    timer fired meanwhile.
  - New public `WebSocketException.HandshakeStatusCode` (`int?`, XML-documented), set through an
    internal constructor.
  - New `ResilientWebSocketClient.IsPermanentConnectFailure`: a 4xx handshake status other than 408
    and 429, or `ProxyAuthenticationRequired` (407), ends the reconnect loop after the error is raised.
    5xx and transport errors keep today's retry behaviour.
- **#84: close/dispose races.**
  - The writer, the extensions and the message assembler are reference counted
    (`_resourceRefCount`, `TryAcquireResources`/`ReleaseResources`). The connection holds one reference
    until `FinalizeClose`. The frame funnel, a data message (around the extension transform) and the
    receive loop each hold one while they work. The last release disposes them.
  - `FinalizeClose` still disposes the stream at once, so a pending read or write faults. It no longer
    disposes the writer (RNG) or the deflate contexts under a concurrent send, and no longer resets
    the assembler under the receive loop.
  - `_sendLock` (and the new `_messageLock`) are never disposed, so a racing `Release` cannot throw
    `ObjectDisposedException`/`SemaphoreFullException` and a waiter cannot hang. Before, the
    Dispose-race test hung its senders.
  - Sends now fail with `WebSocketException(ConnectionClosed)` on a Closing or Closed connection, on
    either side of the lock. Before, the pre-lock check threw `InvalidOperationException`. None and
    Connecting are still `InvalidOperationException` (usage error).
    - `WebSocketSendCancellationTests` asserted the old type after an abort. It is updated.
    - The keep-alive loop now treats a `WebSocketException` while not Open as "closing, stop pinging",
      not as a keep-alive failure.
  - `WebSocketClient.SendAsync` reads the connection once and throws `WebSocketException` when a
    concurrent close/dispose detached it. Before, this was a `NullReferenceException`.
  - `ResilientWebSocketClient.SendAsync` maps an `ObjectDisposedException` from a child that was
    disposed during a reconnect to `WebSocketException(ConnectionClosed)` (R5-N2 from
    `2026-02-phase18-review.md`). If the resilient client itself is disposed, it still throws ODE.
- **#86: close-handshake semantics.**
  - The close reply echoes the received code (RFC 6455 section 5.5.1). A Close without a body (1005,
    local only per section 7.4.1) is answered with an empty Close. The reply is bounded by
    `CloseHandshakeTimeout`.
  - After the Close exchange, the receive loop reads (and discards) until the server closes TCP, or
    until `CloseHandshakeTimeout`. On timeout it disposes the stream (section 7.1.1).
    `AwaitServerTransportCloseAsync` registers the timeout to dispose the stream, so the pooled read
    buffer is returned only after the read completes (the #226 pattern).
  - `CloseAsync` runs in two phases:
    1. Send our Close and wait for the peer's, bounded by `CloseHandshakeTimeout`. On timeout it aborts.
    2. Await the receive loop, which bounds its own FIN wait. A slow FIN after a completed exchange is
       a normal closure, not an abort.
    - If the caller's token cancels either phase, the connection is finalized with 1006 and an
      "aborted" terminal error before `OperationCanceledException` propagates. Before, the connection
      stayed in Closing.
  - Two send locks, always taken in the order `_messageLock` then `_sendLock`:
    - `_messageLock`: one data message at a time. The Close frame takes it too, so a Close never splits
      a message (section 5.5.1 allows finishing the current message first).
    - `_sendLock`: one frame at a time on the shared writer.
    - Data messages are written fragment by fragment, each fragment taking `_sendLock`
      (`WriteMessageFramesAsync`). Ping and pong take only `_sendLock`, so they go out between
      fragments (section 5.4). `SemaphoreSlim` grants a queued async waiter on `Release`, so a waiting
      pong gets the lock before the next fragment.
  - `OutboundFrame` (a readonly struct) replaces the per-send closures.

## Files

- `Runtime/WebSocket/WebSocketHandshakeValidator.cs`, `WebSocketException.cs`, `ResilientWebSocketClient.cs`,
  `WebSocketClient.cs`, `WebSocketConnection.cs`, `WebSocketConnection.Lifecycle.cs`,
  `WebSocketConnection.SendReceive.cs`, `WebSocketConnection.Extensions.cs`. The `Extensions.cs`
  change is limited to the signature of `WriteMessageWithExtensionsAsync` and its one writer call.
- Tests (new): `Tests/Runtime/WebSocket/WebSocketHandshakeRejectTests.cs`, `WebSocketCloseSemanticsTests.cs`,
  `WebSocketLifecycleRaceTests.cs`, `WebSocketLifecycleTestStreams.cs` (ScriptedResponseStream,
  SingleStreamTransport, ControlledClientStream).
- Tests (changed):
  - `InMemoryWebSocketPeer`: closes its transport after the close exchange (default; opt out with
    `CloseTransportAfterCloseHandshake = false` and close later with `CloseTransport()`).
  - `WebSocketSendCancellationTests`: a send after an abort expects `WebSocketException`.

## Decisions

- **How the status reaches the resilient client.** A new public property,
  `WebSocketException.HandshakeStatusCode`, rather than walking `InnerException`. The issue asks for
  the status to be in the error, and callers need it too.
- **What stops reconnecting.** Only the permanent connect failures listed above (4xx except 408/429,
  and 407). The loop does not honour `IsRetryable()` in general, because `HandshakeFailed` is
  non-retryable for every status, which would stop retries on 503 too
  (`ResilientClient_ExhaustsRetries_AndCloses` relies on 503 being retried).
- **Extensions commit a message before its first byte.** When an extension is active, a message is
  committed from the start: the transform already advanced the deflate context (RFC 7692 section
  7.2.1), so a cancellation while waiting for the first fragment's lock aborts. This keeps the old
  behaviour, where the transform ran after `writeStarted`.
- **`DisposeAsync` is unchanged.** It still cannot run its graceful close, because `CloseAsync`
  throws ODE once `_disposed` is set. Filed as #301 (v1.1), since fixing it changes the timing of
  every dispose.

## Deferred

- #301: the `DisposeAsync` graceful close.

## Verification

- `./Tools~/ci/check.sh`: OK (invariants, format, shadow build with analyzers).
- Unity 2021.3.45f2 host editor, PlayMode, `--filter 'WebSocket'`: 210/210 passed, at the final code.
  This includes the 20 new tests, the existing WebSocket suites (integration, reconnect, send
  cancellation, receive-after-close, backpressure, extensions) and the Unity WebSocket tests whose
  names contain "WebSocket". Runtime tests discover nothing in EditMode.
- Fail-before check: I restored `Runtime/WebSocket` from `origin/main` with compile-only stubs and
  ran the new fixtures plus `WebSocketSendCancellationTests` in PlayMode.
  - Every regression test failed:
    - #83 content-length, chunked, invalid length and connect cases hung until the guard.
    - #83 resilient 401 case reconnected 5 times.
    - #86: echo, empty reply, FIN wait, canceled close and pong ordering.
    - #84 races: abort and pong timeout gave `InvalidOperationException`; the Dispose race hung its
      senders.
  - Only the bound-style tests passed there: close-delimited, limit, FIN timeout and resilient
    reconnect.
- Not run: the Autobahn interop job, the full PlayMode/EditMode suites, IL2CPP players, devices.
