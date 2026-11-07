# 2026-11: the abandoned-I/O gate also holds back sockets closed under a loop read (#375)

## What

- **#375 (part).** #369 added `AbandonedSocketIo`: a socket operation abandoned while pending is
  recorded, and `HappyEyeballsConnector` creates no socket until every recorded operation ended or
  expired (`SettleTimeout`, 1 s). It recorded only abandoned operations. The HTTP/2 read loop and
  the WebSocket receive loop instead keep awaiting their read while the owner closes the stream on
  teardown, so on IL2CPP the read's selector job (keyed by the descriptor number) outlived the
  socket without the gate seeing it.
- The owner now records the loop task with `AbandonedSocketIo.Track` right before it closes the
  stream under the loop. The loop ends once its read ended, which opens the gate; a loop that has
  not started or already ended is not recorded (`Track` ignores null and completed tasks).

## Files

- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`: `Dispose()` tracks `_readLoopTask`
  before `DisposeStreamOnce()`.
- `Runtime/WebSocket/WebSocketConnection.cs`, `WebSocketConnection.Lifecycle.cs`: new
  `TrackAbandonedReceiveLoop()` (once per connection, `Interlocked.Exchange` on
  `_receiveLoopTracked`), called in `FinalizeClose` before the stream is disposed.
- `Runtime/WebSocket/WebSocketConnection.SendReceive.cs`: the close-handshake timeout
  (`AwaitServerTransportCloseAsync`, whose read is the receive loop's own) calls it before it
  disposes the stream.
- Tests: `Tests/Runtime/Transport/AbandonedLoopGateTests.cs` (new). It reuses the #369 ordering
  helper, now `internal` in `AbandonedSocketIoTests`. `AbandonedReadStream` gained `OnWrite` and
  `Answer(byte[])` so a test can answer the WebSocket upgrade request with its computed accept key.

## Decisions

- **One owner per Track.** HTTP/2: `Dispose()` past its `_disposed` CompareExchange is the only
  place that closes the stream while the loop can still run. The second `DisposeStreamOnce()`, in
  `FinalizeResourceDispose`, runs after the background tasks completed, so it does not track.
  WebSocket: two places close the stream under the receive loop. A per-connection flag makes the
  first of them record the loop and the other skip it. Abort during the close-handshake read cancels
  the lifecycle token, which runs the timeout registration (it is linked) before `FinalizeClose`
  reaches its own call. The tests assert `PendingCount == 1` after repeated teardown calls.
- **Close-handshake timeout included.** The lane brief listed it as a possible deferral. It is the
  same task (`ReceiveLoopCoreAsync` awaits `AwaitServerTransportCloseAsync`), so it was fixed here
  and not filed separately.
- **Teardown run by the loop itself.** When `FinalizeClose` runs on the receive loop (remote close,
  protocol error), the loop is recorded until it returns, which is immediately after; no read is
  pending then. That is a harmless short hold, bounded by `SettleTimeout`. Nothing on either loop
  waits for a new socket, so the gate cannot wait on itself.
- Fields are read with `Volatile.Read` at the new sites. `_readLoopTask` and `_receiveLoopTask` are
  written once before the loop can be torn down. A teardown that races the assignment sees null and
  records nothing; the loop then starts on a closed stream and its first read fails at once (the
  pre-init window belongs to #162).

## Deferred

- iOS re-run (`needs-device-validation`): the gate's effect (no lost receive after descriptor reuse)
  only shows on IL2CPP iOS. #375 stays open for it (the PR says "Part of #375").

## Verification

- `'./Tools~/ci/check.sh'`: OK (invariants, format, shadow build of 33 assemblies with analyzers).
- Unity 2021.3.45f2 host editor, PlayMode, filter
  `AbandonedLoopGateTests|AbandonedSocketIoTests|WebSocketLifecycleRaceTests|WebSocketCloseSemanticsTests|Http2ConnectionTests`:
  226 passed, 0 failed.
- The same new fixture against the runtime files from before the fix: 3 of 3 failed. Each failed on
  its "recorded" assertion, so each test catches the missing Track.
- Not run: the full Unity suites (periodic host-editor run), an IL2CPP player, the Android emulator,
  and iOS.
