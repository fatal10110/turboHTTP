# 2026-11 — HTTP/2 send-stall detection (#197)

Wave w1101 lane `h2-send-stall`. Follow-up of #14 (h2-write-path, `2026-10-h2-write-path.md`): a started
frame is finished with the connection token only, so a peer that stops reading could hold a frame write,
`_writeLock` and every request deadline behind it with no bound.

## What changed

- `Runtime/Transport/Http2/Http2FrameCodec.cs`: `WriteFrameAsync` records write progress in
  `_writeProgressTimestamp` (`Http2MonotonicClock` timestamp, `Volatile` read/write) before the 9-byte
  header write, before each payload slice and before the flush, and clears it to 0 in a `finally`
  (success, failure, cancellation). The payload is written in slices of at most
  `WriteProgressSliceBytes` (16384, the smallest legal SETTINGS_MAX_FRAME_SIZE), so a large DATA frame
  (peer MAX_FRAME_SIZE up to 2^24-1) records progress per slice instead of counting as one long write.
  Default-sized frames are still one payload write. New `IsWriteStalled(now, timeoutMs)`.
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`: `MaintenanceLoopAsync` calls
  `FailConnectionIfSendStalled()` after the receive-side scan. When the in-flight frame write made no
  progress for the stall timeout it calls `FailAllStreams(UHttpException(NetworkError, "HTTP/2
  connection send stalled ..."))` then `Dispose()` (same pattern as the PING liveness timeout), and the
  loop ends.
- `Runtime/Core/StreamingOptions.cs`: XML doc on `Http2StallTimeoutSeconds` (now also the send bound).
- Tests: `Tests/Runtime/Transport/Http2/Http2ConnectionTests.SendStall.cs` (new);
  `CreateGatedConnectionAsync` in `Http2ConnectionTests.WritePath.cs` takes optional `Http2Options`.

## Decisions

- **Marker in the codec, not in `WriteFrameLockedAsync`.** Every frame write goes through
  `Http2FrameCodec.WriteFrameAsync`, including the read loop's SETTINGS ACK and PING ACK (which bypass
  `WriteFrameLockedAsync`). A stall there blocks the read loop too, so PING liveness cannot catch it;
  the codec marker covers every path with one change and keeps the diff out of ReadLoop.cs. The
  preface/initial SETTINGS in `InitializeAsync` run before the maintenance loop starts and stay bounded
  by the caller's token.
- **Progress, not frame duration.** The marker moves at each header/slice/flush write, so a slow but
  reading peer is never failed while it accepts at least 16 KB per stall timeout.
- **Timeout reuses `Http2StallTimeoutSeconds`** (default 60 s; test override
  `Http2Options.TestStallTimeoutMillisecondsOverride`); no new public option. Worst-case detection is
  stall timeout + maintenance interval (65 s by default).
- **Dispose after FailAllStreams.** Cancelling `_cts` does not abort a socket write on every runtime
  (NetworkStream/SslStream may check the token only at the start); disposing closes the transport stream,
  which does. `SendGoAwayOnDisposeBestEffort` skips the GOAWAY because the stalled writer holds
  `_writeLock` (`Wait(0)`).
- **No early fail on request cancellation** (the issue's optional idea): failing the whole connection
  because one request's deadline expired would fail every other stream on it; the stall timeout already
  bounds the request (AC 2).
- Flow-control waits (`WaitForSendWindowAsync`) are not inside a frame write and are not send stalls;
  they stay bounded by the request token.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity PlayMode `--filter 'Http2ConnectionTests|Http2FrameCodec|Http2FlowControlTests'`: 200/200 passed
  (EditMode with the same filter runs 0 tests: the runtime test asmdef runs under PlayMode).
- With the `FailConnectionIfSendStalled()` call disabled, `DataFrameWriteBlockedByNonReadingPeer_*` and
  `RequestDeadlineExpiresWhileHeadersWriteBlocked_*` fail with "Test watchdog expired".
- Not run: full Unity suites (host editor, periodic), IL2CPP player builds, devices. Real-socket behaviour
  of an aborted write after `Dispose` (NetworkStream/SslStream on Mono/IL2CPP) is not exercised by the
  gated test stream: needs-device-validation.
