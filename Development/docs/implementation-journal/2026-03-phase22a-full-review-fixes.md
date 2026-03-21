# Phase 22a Full Review Fixes

**Date:** 2026-03-21  
**Scope:** Remediation pass for `Development/docs/phases/phase22a/review-22a-full-implementation.md`  
**Status:** Implemented in-repo; Unity batch execution and device validation still deferred to 22a.6

## What was implemented

Closed the actionable review findings across Core, HTTP/1.1, HTTP/2, middleware, cache, and observability:

1. Hardened request-body session lifetime handling.
   - `RequestBodyReadSession.Dispose()` now always releases the session gate even when stream disposal throws
   - `UHttpRequestBody` now faults the body on dispose failure without leaving `_activeSession` stuck
   - added a regression test proving a throwing factory does not permanently lock the body for future opens

2. Restored buffered fast paths through observability wrappers.
   - `ObservedResponseBodySource` now delegates `TryGetBufferedData(...)` and `TryDetachBufferedBody(...)`
   - detached buffered bodies are now observed and finalized without forcing a drain-and-copy fallback
   - `MetricsHandler` now uses pre-bound instance delegates instead of per-request closure lambdas

3. Hardened decompression/cache cleanup paths.
   - removed the shared static overflow probe from decompression limit enforcement
   - `DecompressionBodySource.DisposeAsync()` now drains with a bounded timeout and aborts on stalled cleanup
   - async decompression reads now offload the compression-stream path when a `SynchronizationContext` is present so async callers do not run the hidden synchronous bridge on a pumping thread
   - `TeeBodySource` trailer loading now uses a bounded timeout instead of `CancellationToken.None`

4. Removed HTTP/1.1 streaming hot-path allocations and tightened reuse budgeting.
   - `Http11ResponseBodySource` no longer uses `Task.WhenAny(...)` + `Task.Delay(...)` for cancellable reader operations
   - cancellation now registers a direct close path and maps the resulting terminal read back to the correct cancellation shape
   - chunked dispose-drain budgeting now uses a conservative worst-case wire multiplier so tiny chunks cannot materially exceed the keep-alive reuse cap
   - chunked request serialization no longer flushes after every chunk

5. Hardened HTTP/2 response/body and connection lifecycle behavior.
   - `Content-Length: 0` is now preserved as a real zero-length body
   - `Http2ResponseBodySource` cleanup no longer rewrites completed streams to aborted or emits redundant `RST_STREAM`s after natural completion
   - faulted-body `DrainAsync(...)` now returns cleanly instead of rethrowing from the trailers task
   - per-stream buffer reservation now rejects overflow safely
   - request dispatch no longer allocates `Task.WhenAny(...)` / `Task.Delay(...)` wrappers while waiting for headers
   - connection-level WINDOW_UPDATE work is now coalesced through a tracked scheduler instead of unbounded fire-and-forget tasks
   - `Http2Connection.Dispose()` now closes the stream immediately and disposes remaining primitives after background tasks exit instead of blocking the caller for a fixed 100 ms
   - transport `link.xml` now preserves the explicit `ValueTask<int>` / `ValueTask<Http2ResponseBodyChunk>` instantiations used by the streaming path

6. Closed the direct test/documentation gaps from the review.
   - documented `UHttpStreamingResponse.GetTrailersAsync(...)` completion semantics
   - replaced the allocation-gate reflection dependency on `UHttpStreamingResponse._bodySource` with an internal Core test seam
   - added regressions for request-body reopen after factory failure, sync dispose after EOF, metrics detach fast path preservation, HTTP/2 zero-length responses, and HTTP/2 faulted drain behavior

## Files modified

Runtime:

- `Runtime/Cache/CacheStoringHandler.cs`
- `Runtime/Core/Internal/RequestBodyReadSession.cs`
- `Runtime/Core/ResponseBodyStream.cs`
- `Runtime/Core/UHttpRequestBody.cs`
- `Runtime/Core/UHttpResponse.cs`
- `Runtime/Core/UHttpStreamingResponse.cs`
- `Runtime/Middleware/DecompressionHandler.cs`
- `Runtime/Observability/MetricsHandler.cs`
- `Runtime/Observability/ObservedResponseBodySource.cs`
- `Runtime/Transport/Http1/Http11RequestSerializer.cs`
- `Runtime/Transport/Http1/Http11ResponseBodySource.cs`
- `Runtime/Transport/Http2/Http2Connection.cs`
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
- `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs`
- `Runtime/Transport/Http2/Http2ResponseBodySource.cs`
- `Runtime/Transport/link.xml`

Tests:

- `Tests/Runtime/Core/UHttpRequestBodyTests.cs`
- `Tests/Runtime/Core/UHttpStreamingResponseTests.cs`
- `Tests/Runtime/Observability/MetricsInterceptorTests.cs`
- `Tests/Runtime/Performance/StreamingAllocationGateTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs`
- `Tests/Runtime/Transport/Http2/Http2ConnectionTests.StreamingResponses.cs`

Docs:

- `Development/docs/implementation-journal/2026-03-phase22a-full-review-fixes.md`

## Decisions / trade-offs

1. **HTTP/1.1 chunked drain budgeting uses a conservative wire multiplier instead of parser-level exact accounting.**  
   The review called out decoded-byte budgeting as optimistic. Using the 1-byte-chunk worst case (`5x`) keeps the keep-alive drain probe bounded without pushing chunk-framing bookkeeping deeper into the parser hot path.

2. **Decompression async offload is conditional, not universal.**  
   The decompression wrapper keeps the normal `ReadAsync(...)` path on worker threads, and only offloads when a `SynchronizationContext` is present. That avoids penalizing background readers just to protect Unity main-thread async callers from the compression stream’s hidden sync bridge.

3. **HTTP/2 dispose now prefers non-blocking caller semantics over a fixed synchronous wait.**  
   The old 100 ms waits both under-waited for real shutdown and risked Editor hitches. The new flow cancels, closes the transport stream immediately, and disposes the remaining synchronization primitives after background tasks actually finish.

4. **The new Core streaming test seam stays internal-only.**  
   `UHttpStreamingResponse.BodySourceForTesting` exists strictly to remove fragile reflection from the allocation-gate tests; it is not new public API.

## Validation

### Repository checks

- `git diff --check`

### Local build harness

- attempted a temporary `dotnet build` harness over the touched runtime/test slices
- the harness is not sufficient for full repo validation because this workspace does not include Unity assemblies or the full module graph expected by the package layout
- the harness did catch one real regression during this pass: `StreamingAllocationGateTests.cs` still required `System.Reflection` for `BindingFlags`; that import was restored

### Environment limits

- no Unity `.sln` / generated project files are present in this workspace
- no Unity batch test run was executed in this environment
- no IL2CPP/mobile/physical-device validation was executed here

## Rubric follow-up

Reviewed the final remediation pass explicitly against both mandatory review rubrics:

- `unity-infrastructure-architect`
  - confirmed no new asmdef dependency expansion was introduced
  - confirmed request/response ownership and disposal now release resources deterministically on the reviewed failure paths
  - confirmed the new Core test seam remains internal-only and replaces fragile reflection rather than broadening API surface
  - confirmed the new tests cover the changed lifetime and detach semantics

- `unity-network-architect`
  - confirmed HTTP/1.1 and HTTP/2 cancellation/cleanup changes stay transport-correct and bounded
  - confirmed the HTTP/2 zero-length, completed-dispose, and WINDOW_UPDATE-scheduling fixes preserve framing / flow-control intent
  - confirmed the decompression changes stay middleware-layer and transport-agnostic while avoiding the reviewed async/pumping-thread hazard
  - confirmed AOT preservation was updated for the added streaming `ValueTask` instantiations

## Deferred / next step

1. Run the changed runtime tests in Unity batch mode.
2. Re-run the full 22a.6 validation matrix on IL2CPP/mobile/physical devices, especially decompression behavior, HTTP/2 cleanup timing, and long-lived streaming disposal.

## Follow-up audit

Re-checked the review document on 2026-03-21 after the initial remediation pass and closed the remaining concrete/code-level observations:

1. `Http2Connection.AwaitResponseCompletionOrCancellationAsync(...)` no longer allocates `AsTask()` / `Task.Delay()` / `Task.WhenAny()` wrappers. The request-scoped cancellation registration already owns the cancel-and-RST path, so the wait now directly awaits `stream.CompletionTask`.
2. `MonitorHandler` now uses cached instance delegates for `ObservedResponseBodySource` instead of a per-streaming-response lambda capture.
3. `DecompressionBodySource.BodySourceStream.Read(...)` now includes a debug assertion documenting the intended invariant that the sync bridge must not run on a pumping `SynchronizationContext`.

The remaining HTTP/2 connection-level WINDOW_UPDATE race observation stays intentionally documented/mitigated rather than "fixed" because the current coalescing scheduler, receive-window cap, and write-path serialization already bound the behavior without adding a more invasive protocol-state machine.
