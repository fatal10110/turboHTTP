# 2026-10 — HTTP/2 flow control: wake-ups, negative windows, credit order, stream overflow (#18, #23, #24, #30 part)

## What

- **#18 (P1)**: the shared `SemaphoreSlim _windowWaiter` (one `Release()` per WINDOW_UPDATE, woke one
  arbitrary sender, counts piled up when nobody waited) is gone. `Http2Connection._sendWindowSignal` is
  one broadcast `TaskCompletionSource<bool>` (RunContinuationsAsynchronously), created lazily by the
  first sender that blocks. `WakeSendWindowWaiters()` swaps it out and completes it; every waiter then
  re-checks its own windows, response completion and cancellation. Wakers: connection and stream
  WINDOW_UPDATE, a positive SETTINGS_INITIAL_WINDOW_SIZE delta, `Http2Stream.TrySetResult` /
  `TrySetException` (response dispatched, stream failed or reset, request cancelled before the
  response), `FailAllStreams` (connection failure and Dispose, after `_cts.Cancel()`), and the
  request token (per-wait registration). `WaitForSendWindowAsync` takes the signal before re-reading
  state, so a change published in between cannot be lost. An early final response (e.g. 413 +
  RST_STREAM(NO_ERROR), RFC 9113 §8.1) now stops a blocked upload at once and the dispatch returns
  the response instead of hanging until the request timeout.
- **#23**: an INITIAL_WINDOW_SIZE decrease that leaves a stream window negative is legal (RFC 9113
  §6.9.2); only `> 2^31-1` stays a connection FLOW_CONTROL_ERROR. Applying the SETTINGS values and
  adjusting every open stream now runs under `_streamCreateLock`, the monitor under which
  `DispatchAsync` snapshots `_remoteSettings.InitialWindowSize` and registers the new stream, so a
  stream created concurrently gets the new window exactly once.
- **#24**: `OnStreamChunkConsumedAsync` credits the stream receive window before writing the
  WINDOW_UPDATE and withdraws the credit if the write fails or is cancelled. The padding-only DATA
  path in the read loop uses the same order. The connection-level path
  (`MaybeSendConnectionWindowUpdateAsync`) already credited first and is unchanged.
- **#30 (window-overflow bullet only)**: a stream WINDOW_UPDATE that overflows the stream window is a
  stream error (RFC 9113 §6.9.1): the stream is failed, tracked as reset and gets
  RST_STREAM(FLOW_CONTROL_ERROR); the connection and other streams continue. Connection-window
  overflow stays a connection error.
- `SendBufferedDataAsync` re-checks `IsResponseCompleted` under `_writeLock`: the read loop fails a
  stream before writing its RST_STREAM under the same lock, so no DATA frame follows our own reset.

## Files

- `Runtime/Transport/Http2/Http2Connection.cs` — `_sendWindowSignal` replaces `_windowWaiter`.
- `Runtime/Transport/Http2/Http2Connection.Send.cs` — `WaitForSendWindowAsync`,
  `WakeSendWindowWaiters`, `ThrowIfConnectionClosedForSend`; both wait sites; locked re-check.
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs` — WINDOW_UPDATE wake + stream overflow RST;
  SETTINGS under `_streamCreateLock`, negative windows allowed, wake on positive delta; padding-only
  DATA credit order.
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs` — credit-before-send with rollback;
  `FailAllStreams` wakes waiters; semaphore disposal removed.
- `Runtime/Transport/Http2/Http2Stream.cs` — completion wakes the connection's send-window waiters.
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.FlowControlWakeup.cs` (+ `.meta`) — 9 tests.

## Decisions

- One broadcast signal per connection instead of per-stream waiters: no extra state on pooled
  `Http2Stream`s (no stale wake after pool reuse), and every window change (connection or stream)
  reaches every affected sender. Cost: a stream WINDOW_UPDATE also wakes senders of other streams,
  which re-check two ints and wait again; bounded by the open streams (MAX_CONCURRENT_STREAMS).
  Allocation happens only while a sender is blocked (one TCS per blocking episode, one registration
  per wait when the request token is cancelable); the old code allocated a linked CTS per wait.
- Lock order unchanged: `_writeLock` then `_streamCreateLock` (DispatchAsync); the read loop takes
  `_streamCreateLock` alone, never across an await, and writes the SETTINGS ACK after releasing it.
  `HpackEncoder`'s size-update lock nests inside it as a leaf.
- #30's idle-stream (RFC 9113 §5.1) and first-frame-SETTINGS (§3.4) bullets are not in this change:
  the first touches the HEADERS/DATA paths owned by the h2-stream-lifecycle lane, the second changes
  what every fake server in the suite must send first. #30 stays open for them.

## Deferred

- #30: idle stream ids -> connection PROTOCOL_ERROR; server preface must start with SETTINGS.

## Verification

- `./Tools~/ci/check.sh`: OK (invariants, format, shadow build + analyzers).
- The new tests and the existing `Tests/Runtime/Transport/Http2/**` + `Http2BodySizeTests` (247
  tests) were compiled against the shadow-build assemblies as an ad-hoc net8.0 NUnit project and run
  with `dotnet test`: all pass, the new ones 8 times in a row. Against the pre-change runtime all 9
  new tests fail (watchdog / GOAWAY(FLOW_CONTROL_ERROR) / SETTINGS ACK missing / wrong window).
- Not run: Unity EditMode/PlayMode (lanes do not run Unity; host-editor periodic run), IL2CPP,
  devices.
