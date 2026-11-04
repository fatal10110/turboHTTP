# 2026-11 — Body, 407-drain and WebSocket handshake reads race their token (#345, #226)

## What

#293 (PR #346) made the HTTP/1.1 head read and the CONNECT reply read end as soon as their token
fires, even when Mono/IL2CPP never ends a pending socket read after the socket is disposed. This
entry closes the remaining read sites:

- **HTTP/1.1 body reads** (`Http11ResponseBodySource` content-length, chunked data/size/CRLF/
  trailer lines, read-to-end, `DrainAsync`, the dispose drain). Once the head is parsed,
  `BufferedStreamReader.ReadBodyAsync` races a pending read with a cancellable token. The race
  allocates no Task, TaskCompletionSource or closure per read: the reader is the
  `IValueTaskSource<int>` of the race (`ManualResetValueTaskSourceCore<int>`). The read's
  completion and the token registration settle it through one `Interlocked` state
  (idle / pending / abandoned). The token side completes it on the thread pool, never inside
  `CancellationTokenSource.Cancel`.
- **407 proxy-challenge drain** (`RawSocketTransport.TryDrainProxyChallengeBodyAsync`). It reads
  through the same reader, so it ends on cancel and returns `false`. Its rented drain buffer is
  never the target of the abandoned read.
- **WebSocket upgrade-response head read and error-body read** (`WebSocketHandshakeValidator`).
  A pending read with a cancellable token is raced with `AbandonOnCancel.WaitAsync`. The read buffer
  goes to the GC when its read was abandoned, and the stream is disposed when that read ends.
- **WebSocket proxy CONNECT** was already raced through `ConnectReplyReader` (#346), including the
  407 retry. New tests pin that both end in `OperationCanceledException` and never in
  `ProxyTunnelFailed`.

## Files

- `Runtime/Transport/Http1/Http11ResponseParser.cs`: `BufferedStreamReader` body race
  (`ReadBodyAsync`, `OnBodyReadCompleted`, `AbandonBodyRead`), reader-owned `_bodyBuffer` for large
  reads, and `Dispose` keeping abandoned buffers out of the pool.
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs`: `AwaitReaderOperationAsync` no longer
  registers `CloseBody` per pending read. It closes the body once the read reports cancellation.
- `Runtime/Transport/RawSocketTransport.cs`: `TryDrainProxyChallengeBodyAsync` is `internal` (for
  tests), and its doc states the bound.
- `Runtime/Core/Internal/AbandonOnCancel.cs` (new): the `AbandonableTask` implementation, moved to
  Core so `TurboHTTP.WebSocket` (Core-only) can use it.
- `Runtime/Transport/Internal/AbandonableTask.cs`: forwarder to `AbandonOnCancel`. Callers are
  unchanged.
- `Runtime/WebSocket/WebSocketHandshakeValidator.cs`: raced head read and `ErrorBodySource` read.
- Tests: `Tests/Runtime/Transport/AbandonedReadStream.cs` (a read that ignores token and Dispose,
  with queued synchronous answers), `Tests/Runtime/Transport/Http1/BodyReadCancellationTests.cs`,
  `Tests/Runtime/WebSocket/WebSocketHandshakeReadCancellationTests.cs`.

## Decisions

- **Race in the reader, not in the body source.** All body-phase socket reads (body source, 407
  drain) go through `BufferedStreamReader`. The reader knows which buffer the pending read writes
  into, so it can keep exactly that buffer out of the pool.
- **No per-read allocation.** The issue required body reads to stay allocation-free on the
  non-cancelled path. A synchronously completed read, or a read with a token that cannot be
  cancelled, is returned unchanged (streaming with `CancellationToken.None`, which the allocation
  gates measure). A pending read with a cancellable token costs one token registration. The body
  source made that registration per pending read before (`CloseBody`), and it is now gone, so the
  count is unchanged. No `Task.WhenAny`, TCS or closure is added.
- **Abandonable reads never target caller memory.** A pending read with a cancellable token used
  to go straight into the caller's destination when that was at least 4 KiB. It now goes into a
  reader-owned buffer: `_bodyBuffer`, rented once per body, at most 64 KiB. The bytes are then
  copied out. Otherwise an abandoned read could write into a buffer that the caller already
  returned to the pool on cancellation: `ResponseCollectorHandler`'s 16 KiB rental, the drain
  buffers, the 407 drain buffer. Reads that cannot be cancelled keep the direct path.
- **`AbandonableTask` moved to Core as `AbandonOnCancel`, with a Transport forwarder.**
  `TurboHTTP.WebSocket` may reference only Core (architecture.md), and Core already gives it
  `InternalsVisibleTo`. The forwarder keeps every Transport caller, and any concurrent lane (the
  BouncyCastle glue has no Core IVT), compiling unchanged. A distinct Core name avoids an ambiguous
  simple name in files that import both `Core.Internal` and `Transport.Internal`.
- **Abandoned buffers go to the GC**, as in #346, instead of returning when the read ends. This
  avoids a second ownership hand-off between `Dispose` and the read's completion.
- **Error-body cancellation keeps the #83 rule.** The status code decides the outcome. An abandoned
  error-body read is swallowed with the other best-effort failures, and the bytes read so far are
  kept.

## Not covered (unchanged)

- `Http11ResponseParser.ParseAsync`'s buffered body readers (`ReadFixedBodyAsync`,
  `ReadChunkedBodyAsync`, `ReadToEndAsync`) run in the head phase on the direct path. They are
  reached only from `RawSocketTransport.DispatchOnStreamAsync`/`SendOnStreamAsync`, which have no
  caller (`rg -n -w DispatchOnStreamAsync Runtime`), and from tests.
- The race at the `Dispose` moment: a read that completes in the same instant as a cancellation can
  still be copied out of the reader buffer while `CloseBody` disposes the reader. This existed
  before this change and is not widened by it.

## Verification

- `'./Tools~/ci/check.sh'`: pass (invariants, format, shadow build with analyzers; the awaiter
  `GetResult` runs in the read's continuation, the same pattern `await` compiles to, not a banned
  `TaskAwaiter`).
- New tests fail on the old code. Without the race the reads hang until the 15 s guard, and the
  large-read test sees the caller's array as the read target.
- Unity PlayMode, host editor, filtered: see the PR "Local gates".
- Not run: full EditMode/PlayMode suites (host editor, periodic), IL2CPP on a device. The Mono and
  IL2CPP read behavior is `needs-device-validation`; the #99 matrix rows belong to other lanes.
