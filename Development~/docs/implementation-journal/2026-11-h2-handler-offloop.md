# 2026-11: HTTP/2 response handler chain runs off the read loop (#29)

## What

`Http2Stream.TryStartResponse` (called by the read loop for a response's final HEADERS) started the
handler chain with `_ = InvokeResponseStartAsync(...)`, so decompression, cache, redirect and user
handlers ran synchronously on the read-loop thread up to their first real await. A handler doing
synchronous work stalled WINDOW_UPDATE, PING and every other stream's frames on the connection.

Now `TryStartResponse` queues the start on the thread pool
(`ThreadPool.QueueUserWorkItem(s_invokeResponseStart, this)`, a static `WaitCallback` with the stream
as state, the `Http11ResponseParser` precedent):

- The work item owns one extra lifetime reference (taken before queueing). It replaces the read
  loop's lease that protected the inline call: it is released as soon as the handler's synchronous
  part returned an incomplete `ValueTask`, or after the completion/fault was signalled when the
  handler completed (or threw) synchronously. A queued-but-not-started work item therefore never
  reads a stream that went back to `Http2StreamPool`.
- The task of `InvokeResponseStartAsync` is owned by the work item: every handler failure is caught
  and reported through the stream's completion source (`HandlerCallbackException`), which the
  dispatch awaits, so the task never faults.
- Ordering: `ResponseBodySource` is created and `_responseStarted` set on the read loop before the
  work item is queued, so DATA and trailers only enter the body source the handler later pulls from
  (start before body for the stream), and `Fail`/`Cancel` take the body-source path (fault the body)
  instead of `OnResponseError`; the handler never sees error-before-start or two outcomes.

## Files

- `Runtime/Transport/Http2/Http2Stream.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.HandlerOffLoop.cs` (+ `.meta`)

## Decisions

- **Thread pool, not a per-stream serial scheduler or a "do not block" contract.** The issue offered
  both; the lane note chose the thread pool. Per-stream ordering needs no scheduler because the
  handler is called once per stream and the body is pull-based.
- **No per-frame allocation.** The only new allocation is the thread pool's internal work item, once
  per response start (per stream). The delegate is static.
- `QueueUserWorkItem` (not `Unsafe…`): the execution context flows from the read loop as it did when
  the handler ran inline there.
- Handlers that block still occupy a thread-pool thread (accepted by the lane note); before, they
  occupied the read loop's pool thread and the whole connection.

## Deferred

None.

## Verification

- Regression test `HandlerOffLoop_BlockingResponseStartHandler_DoesNotDelayConcurrentStream`: two
  streams on one connection, stream A's handler blocks synchronously in `OnResponseStartAsync` on an
  event the test opens only afterwards; stream B's dispatch completes and a PING is ACKed while A is
  still blocked. On `origin/main`'s `Http2Stream.cs` it fails ("Test watchdog expired." waiting for B).
- `'./Tools~/ci/check.sh'`: OK.
- Unity PlayMode `--filter 'Http2ConnectionTests|Http2FlowControlTests|StreamingAllocationGate|Http2'`:
  382/382 passed (EditMode has no matching tests).
- Not run: the full Unity suites (periodic host-editor run), IL2CPP player builds, devices.
