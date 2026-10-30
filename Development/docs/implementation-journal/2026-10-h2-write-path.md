# 2026-10 — HTTP/2 write path under one write-lock hold (#12, #13, #14, #27)

## What

- **#12 (P0)**: request trailers are no longer HPACK-encoded ahead of time. `CollectRequestTrailers`
  runs the user trailer provider **outside** `_writeLock` and returns validated fields (or null), so
  the final DATA frame still knows whether it carries END_STREAM. `SendTrailingHeadersAsync` encodes
  and writes the trailer HEADERS/CONTINUATION in one `_writeLock` hold. No API returns
  `HpackEncoder` output past lock release (`PrepareRequestTrailerBlockAsync` is gone). Trailers
  skipped because the response already completed are never encoded, so encoder state is untouched.
- **#13**: `DispatchAsync` acquires `_writeLock` first (the request token only gates this wait).
  Inside that hold it runs the GOAWAY / closed / MAX_CONCURRENT_STREAMS checks and allocates the id
  under `_streamCreateLock`, registers the stream, encodes and writes HEADERS. Ids reach the wire in
  increasing order. A request cancelled while waiting consumes no id and sends nothing. The request
  cancellation callback is registered only after HEADERS are written, so a cancellation RST_STREAM
  can no longer precede the stream's HEADERS. Before this change a request cancelled in the lock
  wait sent RST_STREAM on an idle stream, a connection error under RFC 9113 §6.4.
- **#14**: every frame written by the request path, plus the pooled control frames
  (`SendFrameWithPooledPayloadAsync`), goes through `WriteFrameLockedAsync`. It writes with
  `_cts.Token`, so request cancellation never interrupts a started frame or header block. On any
  write exception it calls `FailAllStreams` (connection dead), wrapping non-UHttp errors in
  `UHttpException(NetworkError)`. `SendBufferedDataAsync` debits the connection and stream send
  windows only after the DATA frame is written (still under `_writeLock`). `DispatchAsync` rethrows
  `OperationCanceledException` only when the request token is cancelled or the connection is still
  open. An OCE caused by connection teardown maps to `UHttpException(NetworkError)`.
- **#27**: `HpackEncoder.SetMaxDynamicTableSize` only records a pending change, under a small
  encoder-private lock. It tracks the minimum since the last block and the final value, and ignores
  a no-op equal to the applied size. `Encode`, which runs under `_writeLock`, applies the change:
  it evicts to the minimum and emits that size first, then the final size (RFC 7541 §4.2, at most
  two updates). The read loop records each HEADER_TABLE_SIZE entry of a SETTINGS frame as it is
  applied, so a lower-then-raise inside a single frame is signalled too. `_lastHeaderTableSize` was
  removed.

## Files

- `Runtime/Transport/Http2/Http2Connection.cs`: Dispatch HEADERS critical section, OCE filter, removed `_lastHeaderTableSize`
- `Runtime/Transport/Http2/Http2Connection.Send.cs`: `WriteFrameLockedAsync`, `FailConnectionAfterWriteError`, trailers, window debit after write
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`: pooled control frames use `WriteFrameLockedAsync`
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`: per-entry HEADER_TABLE_SIZE recording
- `Runtime/Transport/Http2/HpackEncoder.cs`: pending size updates (min then final)
- Tests: `Tests/Runtime/Transport/Http2/Http2ConnectionTests.WritePath.cs`, `HpackEncoderTableSizeTests.cs`, `Helpers/GatedWriteStream.cs`

## Decisions

- The trailer provider (user code) now runs outside `_writeLock`. Before, it ran under the lock.
- The codec still writes the frame header and payload as two writes. Because both use the
  connection token, the frame cannot be split by request cancellation, and the zero-copy DATA path
  stays as it is (no coalescing copy).
- Debit after a successful write, rather than credit back on failure: a failed write kills the
  connection anyway.
- Lock order is `_writeLock` (async) then `_streamCreateLock` (monitor, never held across an await).

## Earlier journal corrected

`2026-03-phase22b.4-review-fixes.md` decision 2 claimed that trailers were encoded and sent under
`_writeLock`. They were encoded under it but sent after it was released. Fixed here (#12).

## Deferred

- #197 (v1.0-beta): no send-stall detection. A started frame write is finished with the connection
  token, so a peer that stops reading can hold that request past its deadline until the connection
  fails.

## Verification

- `./Tools~/ci/check.sh`: OK.
- The `Tests/Runtime/Transport/Http2` NUnit tests (194, all except the Unity-only
  `Http2FlowControlTests.cs`) were compiled into a scratch net8.0 NUnit project against the
  shadow-built DLLs: all pass, and the new tests passed 5 runs in a row. The same harness against
  the origin/main runtime: 10 of the new tests fail (trailer foreign bytes, trailer desync, both
  mid-frame cancels, id consumption plus the idle-stream RST, min-then-final at encoder and
  connection level, the concurrent resize stress test). The 200-dispatch monotonic-id test does
  not reproduce the race locally on old code (it needs a preemption between allocation and the
  lock wait). The deterministic id test covers the allocation point instead.
- NOT run: the Unity EditMode/PlayMode suites (Unity tests: CI), IL2CPP, devices (nothing in this
  change is device-specific; IL2CPP tracked in #99).
