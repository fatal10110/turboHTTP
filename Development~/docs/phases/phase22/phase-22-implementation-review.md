# Phase 22: Interceptor Architecture Redesign — Implementation Review

**Date:** 2026-03-15
**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Scope:** Full Phase 22 implementation (sub-phases 22.1–22.4)
**Review pass:** Pass 7 (full dual-agent review over all sub-phases)
**Verdict:** CONDITIONALLY APPROVED — 3 blockers require fixes (1 new from Pass 7, 2 carried from Pass 6)

---

## Executive Summary

Phase 22 replaces the ASP.NET Core-style middleware pipeline (`IHttpMiddleware` / `HttpPipeline`) with an undici-style interceptor model (`IHttpHandler` push callbacks + `IHttpInterceptor` dispatch wrapping). The implementation spans four sub-phases:

- **22.1 (Core Interfaces):** APPROVED (4 review passes)
- **22.2 (Transport Adaptation):** APPROVED (3 review rounds)
- **22.3 (Interceptor Rewrites):** CONDITIONALLY APPROVED (5 passes, all actionable findings fixed)
- **22.4 (Testing Adaptation):** Implemented, pending Unity validation

Pass 6 was a fresh comprehensive review across all sub-phases with both specialist agents. It identified 2 blockers and 7 warnings. Pass 7 is a second full dual-agent review that identified **1 new blocker**, **5 new HIGH findings**, **9 new MEDIUM findings**, and **9 new LOW findings**. All 15 previously confirmed PASS areas remain valid with no regressions. 3 new PASS areas added in Pass 7.

One known design limitation remains intentionally deferred: `DecompressionHandler` still buffers the compressed body before replaying decompressed chunks. This is already called out in the phase overview as an accepted Phase 22 limitation.

---

## Sub-Phase Review History

### 22.1: Core Interfaces & Pipeline
- **Passes:** 4
- **Verdict:** APPROVED
- **Key findings (all closed):** `DispatchBridge` internal visibility (fixed with `TransportDispatchHelper` public shim), `HttpHeaders.Empty` mutable singleton (fixed with `_frozen` flag), `RawSocketTransport.SendAsync` visibility (made internal), null interceptor handling (throws `ArgumentException`)

### 22.2: Transport Adaptation
- **Rounds:** 3
- **Verdict:** APPROVED
- **Key findings (all closed):** 3 P0 compile/leak fixes, 2 P1 correctness fixes, 3 P2 protocol/observability fixes, 4 P3 recommended fixes. 6 items deferred.

### 22.3: Interceptor Rewrites
- **Passes:** 5
- **Verdict:** CONDITIONALLY APPROVED
- **Key findings (all closed):** 2 compile blockers, 9 high-severity fixes (decompression error handling, retry/redirect safety, observability allocations, cache background work), 10 medium-severity fixes (clone-on-write, monitor capture, background networking), 4 low-severity fixes. 1 blocker + 6 actionable warnings from Pass 4 fixed in Pass 5.

### 22.4: Testing Adaptation
- **Verdict:** Implemented, pending Unity validation
- **Key changes:** `MockTransport` and `RecordReplayTransport` converted to `DispatchAsync`, `TestInterceptorPipeline` replaces `HttpPipeline`, test files renamed from `*Middleware*` to `*Interceptor*`

---

## Pass 6 Findings

### BLOCKER (2)

#### P6-B1: `CacheInterceptor.AwaitPendingMutationAsync` — faulted mutation propagates into read path

**Source:** Infra
**File:** `Runtime/Cache/CacheInterceptor.cs` lines 1059–1088

Faulted pending mutation task propagates its exception into the read/lookup path. If a background storage write fails, `await pendingTask` at line 1072 rethrows, aborting a subsequent cache lookup for the same key. `AwaitPendingMutationAsync` is a sequencing barrier, not a result consumer — faults should be suppressed.

The `RunQueuedMutationAsync` continuation (lines 1099–1103) already swallows prior-task failures for chaining purposes, but `AwaitPendingMutationAsync` bypasses this.

**Fix:** Wrap `await pendingTask` in try/catch that swallows faults:

```csharp
private async Task AwaitPendingMutationAsync(string baseKey, CancellationToken cancellationToken)
{
    // ... existing lookup ...

    if (pendingTask.IsCompleted || !cancellationToken.CanBeCanceled)
    {
        await AwaitIgnoringFaultAsync(pendingTask).ConfigureAwait(false);
        return;
    }

    // ... existing WhenAny + cancellation logic ...

    await AwaitIgnoringFaultAsync(pendingTask).ConfigureAwait(false);
}

private static async Task AwaitIgnoringFaultAsync(Task task)
{
    try { await task.ConfigureAwait(false); }
    catch { /* write failures are logged by QueueBackgroundWork; reads must not abort */ }
}
```

---

#### P6-B2: `AssertAsync.ThrowsAsync` — synchronous `GetResult()` on test thread

**Source:** Infra
**Files:** `Tests/Runtime/AssertAsync.cs` lines 30, 52, 73, 94; used in `DecompressionInterceptorTests.cs`, `RedirectInterceptorTests.cs`, `CacheInterceptorTests.cs`, etc.

`AssertAsync.ThrowsAsync` calls `asyncDelegate().GetAwaiter().GetResult()` directly on the test thread instead of using `Task.Run(...)` like `AssertAsync.Run` does. Risks Unity main-thread deadlock when awaited continuations post back to `SynchronizationContext`. In Unity's Editor test runner, this can cause a deadlock when the awaited continuation posts back to the main thread while the main thread is blocked in `.GetResult()`.

**Fix:** Align with `AssertAsync.Run`:

```csharp
public static T ThrowsAsync<T>(Func<Task> asyncDelegate) where T : Exception
{
    try
    {
        Task.Run(asyncDelegate).GetAwaiter().GetResult();
    }
    catch (T expected) { return expected; }
    catch (Exception ex)
    {
        throw new Exception($"Expected {typeof(T).Name}, got {ex.GetType().Name}.", ex);
    }
    throw new Exception($"Expected {typeof(T).Name}, but no exception was thrown.");
}
```

---

### WARNING (7)

#### P6-W1: `IHttpHandler` XML doc gap for partial-data + error sequence

**Source:** Both
**File:** `Runtime/Core/IHttpHandler.cs` lines 26–32

The `OnResponseError` doc correctly states it "may be called... after `OnResponseData`" but doesn't explicitly call out the partial-body case for third-party handler implementors. Custom handlers stacked between decompression and the collector may receive `OnResponseStart` + partial `OnResponseData` + `OnResponseError`. The contract doc should highlight this sequence explicitly.

**Recommendation:** Add a note: "After `OnResponseStart` and possibly partial `OnResponseData` calls, this may still fire for mid-body failures (e.g., decompression limit exceeded, connection reset). Implementations must handle all callback orderings."

---

#### P6-W2: `RedirectHandler.CompleteWithEnd`/`CompleteWithError` — unnecessary `throw` after TCS fault

**Source:** Infra
**File:** `Runtime/Middleware/RedirectHandler.cs` lines 233–258

`throw` at lines 243/257 after `_completion.TrySetException(ex)` is unnecessary — the TCS already captured the exception. The re-throw creates a dual-fault path: the transport task faults with the same exception, and `BridgeDispatchCompletion` calls `TrySetException` again (silently fails since TCS is already completed). Harmless today due to `TrySet*` guards, but fragile for future changes.

**Fix:** Remove the `throw` statements from both `CompleteWithEnd` and `CompleteWithError`.

---

#### P6-W3: `MonitorHandler.OnResponseData` — per-chunk `Volatile.Read` overhead

**Source:** Infra
**File:** `Runtime/Observability/MonitorHandler.cs` lines 47–63

Reads `MonitorInterceptor.GetBufferedResponseCaptureLimit()` (2x `Volatile.Read` + 2x `Math.Max`) on every `OnResponseData` chunk. For large streaming responses with many small chunks this is measurable overhead. The capture limit is an application-lifetime constant in practice.

**Fix:** Cache the capture limit once in `OnResponseStart` and store as a field.

---

#### P6-W4: `CacheStoringHandler.OnResponseEnd` — store failure discards successful response

**Source:** Infra
**File:** `Runtime/Cache/CacheStoringHandler.cs` lines 95–103

If `QueueStoreResponse` throws (e.g., OOM in background task queue allocation), the exception propagates out, causing a successfully-received response to be discarded even though `_inner.OnResponseEnd` already completed successfully. The response was fully delivered — the store failure should be swallowed and logged, not re-thrown.

**Fix:** Catch and log exceptions from `QueueStoreResponse` rather than re-throwing:

```csharp
try
{
    _owner.QueueStoreResponse(..., bodyToStore);
}
catch (Exception ex)
{
    bodyToStore?.Dispose();
    // Store queueing failed; response delivery already succeeded.
}
```

---

#### P6-W5: `RetryDetectorHandler` — 5xx body drop and connection drain concern

**Source:** Both
**File:** `Runtime/Retry/RetryDetectorHandler.cs` lines 25–31

On 5xx, silently drops `OnResponseData`/`OnResponseEnd` without draining. For HTTP/1.1 with Content-Length body, the transport must drain the body from the socket before connection reuse. If `RawSocketTransport` relies on handler callbacks to drain, dropping them here could cause connection pool corruption or misparse on the next request.

**Action required:** Verify `RawSocketTransport`'s behavior when `OnResponseData` and `OnResponseEnd` are silently discarded. If draining is handler-callback-driven, `RetryDetectorHandler` must explicitly signal abandonment (or the transport must have a separate drain path).

---

#### P6-W6: `RedirectInterceptor.CopyBodyForRedirect` — shared array reference

**Source:** Network
**File:** `Runtime/Middleware/RedirectInterceptor.cs` lines 222–236

`MemoryMarshal.TryGetArray` path returns the original backing array by reference — if the original request is later disposed or the body array reused (e.g., from a pool), the redirect request sees corrupted data. Currently safe because `UHttpRequest` doesn't pool body arrays in Phase 22, but the shared-reference assumption is undocumented and fragile.

**Recommendation:** Document the assumption or always copy.

---

#### P6-W7: `MockTransport.DispatchAsync` — response lifetime overlap in fallback path

**Source:** Network
**File:** `Runtime/Testing/MockTransport.cs` lines 244–266

`DriveHandler(response, handler, context)` is called, then `response?.Dispose()` in `finally`. If `DriveHandler` enqueues body data that references `response.Body`, and `response.Dispose()` releases that body, downstream reads would see freed memory. Currently safe because `ResponseCollectorHandler.OnResponseData` copies into its own `SegmentedBuffer`, but the lifetime overlap is worth documenting.

---

### INFO (5)

#### P6-I1: `DispatchBridge.AttachCompletion` — correctly designed

**Source:** Infra
**File:** `Runtime/Core/Pipeline/DispatchBridge.cs` lines 70–111

`ExecuteSynchronously` + `RunContinuationsAsynchronously` TCS correctly prevents double-continuation risk. No action needed.

---

#### P6-I2: `CacheInterceptor.Dispose` — no background work drain

**Source:** Infra
**File:** `Runtime/Cache/CacheInterceptor.cs` lines 801–809

`_backgroundWorkCancellation` is cancelled but in-flight background work is not awaited before storage disposal. Storage implementations should handle post-dispose calls gracefully with `ObjectDisposedException`. Flag for `v1.1` to add `IAsyncDisposable` with drain logic.

---

#### P6-I3: Inconsistent async test patterns

**Source:** Infra
**Files:** Multiple test files including `CacheInterceptorTests.cs`, `RedirectInterceptorTests.cs`, `MockTransportTests.cs`

Some tests use `AssertAsync.Run` correctly; others bypass it with raw `Task.Run(async () => {...}).GetAwaiter().GetResult()`. Recommend standardizing on `AssertAsync.Run` for all async test bodies.

---

#### P6-I4: Cookie merge allocations

**Source:** Network
**File:** `Runtime/Middleware/CookieInterceptor.cs` lines 96–97

`HashSet<string>` + `List<string>` allocated per merge operation on every request with both user-supplied and jar-supplied cookies. Acceptable for v1 (not a hot path). Previously tracked as P4-W9.

---

#### P6-I5: `DecompressionHandler.Crc32Table` — eager static allocation

**Source:** Infra
**File:** `Runtime/Middleware/DecompressionHandler.cs` line 13

Static 1KB `uint[256]` allocated at class load even when decompression is disabled. Minor; consider `Lazy<uint[]>`.

---

## Pass 7 Findings

### BLOCKER (1)

#### P7-B1: Handler callback exceptions fault dispatch task and may corrupt transport state

**Source:** Infra
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`

Handler callbacks (any `IHttpHandler` method) can throw exceptions that propagate synchronously up through the handler chain into the transport's dispatch loop. This bypasses the error delivery contract (the dispatch task faults with a non-`UHttpException`) and may leave transport state inconsistent (e.g., partially consumed TCP streams on HTTP/1.1, unfinished HTTP/2 stream). No safety wrapper exists at the transport or `DispatchBridge` level.

This is distinct from the `ResponseCollectorHandler.TrySetException` behavior (which is correct) — the issue is that *any* interceptor handler that throws from a callback will fault the dispatch task, and transports do not guard against this.

**Fix:** Add a `HandlerCallbackSafetyWrapper` in `DispatchBridge` (or at transport entry) that intercepts exceptions from handler callbacks during the synchronous drive phase and converts them to `handler.OnResponseError(Wrap(ex), ctx)`. This is safer than requiring every future transport author to remember this rule. The `DispatchBridge.AttachCompletion` continuation already handles task-level faults via `collector.Fail()` — extend that same safety to the synchronous callback-invoke path.

---

### HIGH (5)

#### P7-H1: `PluginContext.ForResponseData` signature uses sampled hash — read-only enforcement bypassable

**Source:** Infra
**File:** `Runtime/Core/PluginContext.cs` lines 694–701

`ResponseEventSignature.ForResponseData` only hashes the first 8 and last 8 bytes plus the length. Two different data chunks of the same length with identical prefix/suffix bytes produce equal signatures, allowing a `ReadOnlyMonitoring` plugin to mutate mid-chunk body data undetected. This is the sole enforcement mechanism for read-only plugin capability.

**Fix:** Use a full CRC-32 or xxHash over the chunk data instead of sampling. The `DecompressionHandler` already has a CRC-32 implementation that could be reused.

---

#### P7-H2: `DecompressionInterceptor.Wrap` — `context.UpdateRequest` not restored on success path

**Source:** Infra
**File:** `Runtime/Middleware/DecompressionInterceptor.cs` lines 41–66

When `Accept-Encoding` is not already set, the interceptor clones the request and calls `context.UpdateRequest(requestForNext)`. The `catch` block restores the original request, but the success path does not — leaving the context pointing to the clone with injected `Accept-Encoding`. In redirect scenarios, subsequent interceptors observe the mutated context. The catch/success path asymmetry is a latent correctness risk.

**Fix:** Add a `finally` block that restores `context.UpdateRequest(request)` when `requestForNext != request`, or document that context request state is expected to reflect the interceptor-mutated version after dispatch.

---

#### P7-H3: `RetryDetectorHandler` — 5xx body suppression relies on undocumented transport assumption

**Source:** Infra
**File:** `Runtime/Retry/RetryDetectorHandler.cs` lines 26–33

The comment states HTTP/1.1 and MockTransport "fully parse/drain the body before invoking handlers." This is a load-bearing assumption. If any future transport drives `OnResponseData`/`OnResponseEnd` streaming (before full body parse), `RetryDetectorHandler` silently discards those chunks. For HTTP/2 multiplexed streams, the stream would need to be properly RST'd if a retry occurs mid-stream.

**Fix:** Add runtime validation that the transport assumption holds, or add an explicit drain handler for 5xx bodies. At minimum, document this as a constraint on `IHttpHandler` implementations for retry-aware handlers.

---

#### P7-H4: `RedirectHandler.OnResponseEnd` — dispatch exception leaves inner handler without terminal callback

**Source:** Infra
**File:** `Runtime/Middleware/RedirectHandler.cs` line 160

When redirect dispatch fails synchronously, `_completion.TrySetException(ex)` is called but no `_inner.OnResponseError(error, context)` fires. If the inner handler already received `OnRequestStart` from the previous hop, it ends up without a terminal callback (`OnResponseEnd` or `OnResponseError`), violating the handler contract.

**Fix:** In `BridgeDispatchCompletion` / the catch paths, ensure `_inner.OnResponseError(error, context)` is called if `!_committed` and any partial callbacks have been delivered to `_inner`.

---

#### P7-H5: `ResponseCollectorHandler.OnResponseError` — race window with `CompleteBufferedResponse`

**Source:** Network
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` lines 78–83

`OnResponseError` calls `DisposeBufferedState()` then `_tcs.TrySetException`. There is a race window: `CompleteBufferedResponse` (from `DispatchBridge` continuation) could check `_tcs.Task.IsCompleted`, see it NOT completed (before `TrySetException`), then call `DetachBufferedResponse()` which returns null (already disposed). This could result in a misleading "Pipeline completed without delivering a response" error instead of the actual transport error.

**Fix:** Call `_tcs.TrySetException` BEFORE `DisposeBufferedState()`, or check if `TrySetException` succeeded and only dispose on success.

---

### MEDIUM (9)

#### P7-M1: `ResponseCollectorHandler.OnResponseError(null)` — unhelpful error message

**Source:** Infra
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` line 81

When `error` is null (caller bug), creates `UHttpException(Unknown, "Unknown response error.")`. Add a defensive null guard or document that null is invalid on the `IHttpHandler.OnResponseError` contract.

---

#### P7-M2: `CacheStoringHandler.OnResponseEnd` — double-dispose risk for `bodyToStore`

**Source:** Infra
**File:** `Runtime/Cache/CacheStoringHandler.cs` lines 65–104

If `QueueStoreResponse` successfully enqueues the task (taking ownership of `bodyToStore`) before throwing on a subsequent operation, both `StoreResponseAsync`'s `finally` and the catch block's `bodyToStore.Dispose()` will dispose the buffer.

**Fix:** Set `bodyToStore = null` immediately after successful `QueueStoreResponse` to prevent double-dispose.

---

#### P7-M3: `PluginContext._requestMutationBaseline` — struct write without synchronization

**Source:** Infra
**File:** `Runtime/Core/PluginContext.cs` line 340

`_requestMutationBaseline` is a multi-field struct written without synchronization in `GuardedNextAsync`'s `finally` block. Under concurrent re-dispatch (`AllowRedispatch`), partial struct writes on ARM are observable.

**Fix:** Fold the baseline update into the existing `_responseGate` lock scope.

---

#### P7-M4: `DecompressionHandler` — double-dispose of `ReadOnlySequenceStream`

**Source:** Infra
**File:** `Runtime/Middleware/DecompressionHandler.cs` lines 123–157

`GZipStream(compressedStream, leaveOpen: false)` disposes `compressedStream` when the `GZipStream` is disposed. The outer `using var compressedStream` also disposes it. Double-dispose is idempotent today via `Stream` base class, but violates ownership model.

**Fix:** Use `leaveOpen: true` in `CreateSingleDecompressionStream` and manage disposal of `compressedStream` explicitly.

---

#### P7-M5: Test files use `Task.Run` directly instead of `AssertAsync.Run`

**Source:** Infra
**Files:** `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs` and others

Some tests use `Task.Run(async () => { ... }).GetAwaiter().GetResult()` directly instead of the `AssertAsync.Run` wrapper. Inconsistent with the test utility pattern and may behave differently in Unity Test Runner.

**Fix:** Migrate all direct `Task.Run` test patterns to `AssertAsync.Run`.

---

#### P7-M6: `DecompressionHandler` — add compressed body size limit

**Source:** Network
**File:** `Runtime/Middleware/DecompressionHandler.cs` lines 61–76

`OnResponseData` accumulates all compressed chunks into `SegmentedBuffer` without a size check. Only the decompressed size is checked. A 100MB compressed body that decompresses to 100MB passes both checks, but the 100MB buffer allocation happens before any size enforcement.

**Fix:** Add a `_maxCompressedBodySizeBytes` check in `OnResponseData` to fail early on unexpectedly large compressed payloads.

---

#### P7-M7: `RedirectHandler` delivers multiple `OnRequestStart` per redirect hop

**Source:** Network
**File:** `Runtime/Middleware/RedirectHandler.cs` lines 157–161

The downstream transport calls `OnRequestStart` on the `RedirectHandler` for each redirect hop, which forwards to `_inner.OnRequestStart`. The `IHttpHandler` contract says "Always the first callback" (implying once). Not a correctness bug but semantically imprecise.

**Fix:** Document in `IHttpHandler` contract that interceptors performing re-dispatch may deliver multiple `OnRequestStart` callbacks.

---

#### P7-M8: `DecompressionInterceptor` clone not disposed on async fault path

**Source:** Network
**File:** `Runtime/Middleware/DecompressionInterceptor.cs` lines 41–67

The catch block handles synchronous dispatch exceptions, but if `next()` returns a Task that later faults asynchronously, the cloned request is never disposed and the original is never restored.

**Fix:** Attach a continuation or use try/finally around the awaited Task to restore and dispose on all paths.

---

#### P7-M9: `Http2Connection.DispatchAsync` never calls `OnRequestStart`

**Source:** Network
**File:** `Runtime/Transport/Http2/Http2Connection.cs` lines 186–338

`OnRequestStart` is only called by `RawSocketTransport.DispatchAsync`. If `Http2Connection.DispatchAsync` is called directly (e.g., in tests), `OnRequestStart` is missing.

**Fix:** Add a clarifying comment that `OnRequestStart` is the caller's responsibility.

---

### LOW (9)

#### P7-L1: `LoggingHandler._bodyPreview` — heap-allocated per request

**Source:** Both
**File:** `Runtime/Observability/LoggingHandler.cs` line 91

`_bodyPreview = new byte[MaxPreviewBytes]` (500 bytes) allocated on every logged request with body. Should use `ArrayPool<byte>.Shared.Rent(MaxPreviewBytes)` and return in terminal callback.

---

#### P7-L2: Bare `catch` swallows fatal CLR exceptions

**Source:** Infra
**Files:** `Runtime/Middleware/DecompressionInterceptor.cs` line 57, `Runtime/Auth/AuthInterceptor.cs`, `Runtime/Middleware/DefaultHeadersInterceptor.cs`

Bare `catch` without type filter catches `OutOfMemoryException` and (on Mono) `StackOverflowException`.

**Fix:** Replace `catch` with `catch (Exception)` to exclude fatal exceptions.

---

#### P7-L3: `MockTransport` — null fallback response crashes outside error delivery contract

**Source:** Infra
**File:** `Runtime/Testing/MockTransport.cs` lines 244–271

If `_fallbackHandler` returns null, `DriveHandler` throws `InvalidOperationException` that propagates as a faulted task without calling `OnResponseError`.

**Fix:** Add null check before `DriveHandler` and call `handler.OnResponseError` in that case.

---

#### P7-L4: `CacheInterceptor.ServeCachedEntry` — `OnRequestStart` semantically incorrect for cache hits

**Source:** Infra
**File:** `Runtime/Cache/CacheInterceptor.cs` line 739

For cache hits, `OnRequestStart` misleads downstream handlers into counting this as an outbound network request. Not a correctness bug since metrics count in `Wrap`, not in `OnRequestStart`.

**Fix (optional):** Document that cache hits drive `OnRequestStart` as a response-delivery signal, not a network dispatch signal.

---

#### P7-L5: `ReadOnlySequenceStream.CanSeek` returns false

**Source:** Network
**File:** `Runtime/Core/Internal/ReadOnlySequenceStream.cs` line 22

Not an issue for `GZipStream`/`DeflateStream` but limits future decompression algorithm compatibility (e.g., Brotli).

---

#### P7-L6: `MockTransport.SendAsync` — extra allocations via `CollectResponseAsync` bridge

**Source:** Network
**File:** `Runtime/Testing/MockTransport.cs` lines 200–208

Creates `ResponseCollectorHandler`, TCS, `SegmentedBuffer` per test call. Acceptable for test-only code.

---

#### P7-L7: `MonitorHandler` clones response headers on every `OnResponseStart`

**Source:** Network
**File:** `Runtime/Observability/MonitorHandler.cs` line 35

Necessary for correctness (headers may be modified by downstream handlers like decompression removing `Content-Encoding`). Documented for awareness.

---

#### P7-L8: `CacheStoringHandler` clones request headers eagerly

**Source:** Network
**File:** `Runtime/Cache/CacheStoringHandler.cs` line 35

`_requestHeaders = request.Headers.Clone()` in constructor even if response is non-cacheable. Could defer to `OnResponseEnd` but current approach is simpler and safer.

---

#### P7-L9: `MonitorHandler` buffers the full text cap before it knows the body is binary

**Source:** Both
**File:** `Runtime/Observability/MonitorHandler.cs` lines 36–64

The response-side monitor buffer is now bounded, but it is still sized from the global max capture limit before the code knows whether the payload is binary. With the default settings, editor monitoring can therefore retain roughly 5MB per response even when the final snapshot will keep only the 64KB binary preview.

**Fix:** Derive the live buffer cap from `Content-Type` in `OnResponseStart` when the payload is clearly binary, and fall back to the larger capture limit only when the payload type is ambiguous.

---

## PASS — Verified Properties (confirmed correct by both reviews)

| ID | Area | Description |
|----|------|-------------|
| P6-PASS-1 | Error delivery | All handlers use `OnResponseError` callbacks, not exceptions. `ResponseCollectorHandler` translates to `TrySetException` on TCS. |
| P6-PASS-2 | Clone-on-write | All 5 request-mutating interceptors (Auth, DefaultHeaders, Cookie, Adaptive, Decompression) clone before mutation and call `context.UpdateRequest()`. |
| P6-PASS-3 | Redirect RFC 9110 | 301/302 POST→GET with body stripping, 303 conversion, 307/308 preserve method, cross-origin credential stripping, HTTPS downgrade blocking, fragment inheritance, loop detection via visited set. |
| P6-PASS-4 | Decompression memory | `SegmentedBuffer` + `ReadOnlySequenceStream` avoids LOH. 64KB ArrayPool buffer returned in `finally`. |
| P6-PASS-5 | Cache boundaries | Uses public `TransportDispatchHelper.CollectResponseAsync` (not internal `DispatchBridge`). Cache hits short-circuit via direct handler callbacks. |
| P6-PASS-6 | Cancellation | All interceptors propagate `CancellationToken` to `next()`. `ConcurrencyInterceptor` passes to `AcquireAsync`. `BackgroundNetworkingInterceptor` uses linked CTS. |
| P6-PASS-7 | Semaphore safety | `ConcurrencyInterceptor` releases semaphore in `finally`. Idempotent disposal via `Interlocked.CompareExchange` CAS. |
| P6-PASS-8 | MockTransport | `DispatchAsync` calls `OnRequestStart` before any response path. Full handler lifecycle (start/data/end) or error path. |
| P6-PASS-9 | Thread safety | `ResponseCollectorHandler` uses `_bodyGate` lock for concurrent `OnResponseData`/`DetachBody`. `_bodyClosed` flag prevents writes after detach. |
| P6-PASS-10 | IL2CPP/AOT | No reflection, `Volatile.Read/Write` for ARM, `Interlocked` for atomic ops, static lambdas, `ConcurrentDictionary` — all IL2CPP compatible. |
| P6-PASS-11 | Redirect completion | `_completion` TCS uses `RunContinuationsAsynchronously`. `BridgeDispatchCompletion` handles faulted/cancelled/succeeded. |
| P6-PASS-12 | Assembly boundaries | All optional modules depend only on Core. No cross-module references introduced. |
| P6-PASS-13 | Retry pattern | Fresh detector per attempt, terminal observer on last attempt, exhaustion telemetry correct, idempotency check before retry loop. |
| P6-PASS-14 | `DispatchBridge` | `ExecuteSynchronously` + `RunContinuationsAsynchronously` TCS correctly designed. |
| P6-PASS-15 | `BackgroundNetworkingInterceptor` | Linked CTS always disposed in `finally`, `scope.DisposeAsync()` awaited, `BackgroundRequestQueuedException` correctly handled as fault not cancellation. |
| P7-PASS-1 | Handler callback ordering | All transports and interceptors follow `OnRequestStart` → `OnResponseStart` → `OnResponseData*` → `OnResponseEnd` (or `OnResponseError` at any point after start). Verified in `RawSocketTransport`, `Http2Stream`, `MockTransport`, `CacheInterceptor`, all handler implementations. |
| P7-PASS-2 | Error delivery contract | Transport errors → `handler.OnResponseError()` + normal task completion; cancellation → `OperationCanceledException`. Verified across `RawSocketTransport`, `Http2Connection`, `MockTransport`, all interceptors. |
| P7-PASS-3 | Platform compatibility | No reflection, no dynamic codegen, `ArrayPool<ResponseEventSignature>` safe for blittable struct, static `Func` delegates for `ConcurrentDictionary`, `SegmentedBuffer`/`ArrayPool<byte>` consistent with prior phases. IL2CPP/AOT: PASS. |

---

## Deferred / Accepted (consolidated across all passes)

| ID | Severity | Description | Target |
|----|----------|-------------|--------|
| H-4 | HIGH | `DecompressionHandler` still buffers the full compressed body before decompression. Documented Phase 22 limitation requiring larger streaming design change. | Follow-up / v1.1 |
| P4-W4 | WARNING | Clone-dispose fragility — safe in current code (all interceptors copy locally before yielding), but undocumented assumption. | Accepted v1 |
| P4-W6 | WARNING | `ExecuteSynchronously` nesting in redirect `BridgeDispatchCompletion` — bounded by max 10 hops. | Accepted v1 |
| P4-W7 | WARNING | Retry multiple `OnRequestStart` — detector wraps real handler, custom handlers below retry not a supported pattern. | Accepted v1 |
| P4-W9 | WARNING | Cookie merge allocations (`HashSet` + `List` per merge). | Deferred Phase 24 |
| P4-W10 | WARNING | `OrderBy().ToArray()` inside `_indexLock` for Vary signatures — trivial for typical 1–3 headers. | Accepted v1 |
| 22.2-D1–D6 | VARIOUS | 6 items deferred from 22.2 transport review (see 22.2 implementation review for details). | Tracked |

---

## Remaining Validation

| ID | Severity | Description | Target |
|----|----------|-------------|--------|
| V-1 | HIGH | Unity Editor compile/test execution has not been run from this workspace | Phase 22 validation |
| V-2 | HIGH | IL2CPP/mobile validation for redirect, retry, cache, decompression, and background work remains pending | Phase 22 validation |
| V-3 | MEDIUM | Middleware-era test/file naming cleanup is still deferred | Phase 22.4 |
| V-4 | MEDIUM | Editor monitor replay-builder test remains ignored after the monitor rewrite | Phase 22.4 |

---

## Validation Criteria Status

| Criterion | Status |
|-----------|--------|
| **22.1:** Core assembly compiles with zero `IHttpMiddleware`/`HttpPipeline` references | PASS |
| **22.1:** `new UHttpClient()` constructs without exception | PASS |
| **22.1:** Zero-interceptor `InterceptorPipeline` dispatches directly to transport | PASS |
| **22.1:** New API surface compiles: `UHttpRequest.Clone()`, `RequestContext.CreateForBackground(UHttpRequest)`, `PluginCapabilities.AllowRedispatch` | PASS |
| **22.2:** Error delivery contract validated (transport error → `OnResponseError` + normal Task; cancellation → exception) | PASS |
| **22.3:** All interceptor unit tests pass (static review — Unity execution pending) | PENDING V-1 |
| **22.3:** `DecompressionInterceptorTests` pass (gzip, deflate, passthrough, header stripping, disposal on error) | PENDING V-1 |
| **22.3:** `RetryInterceptorTests` pass (5xx, transport error, idempotency, exhaustion) | PENDING V-1 |
| **22.3:** `RedirectInterceptorTests` pass (all status codes, cross-origin, downgrade, loop, completion bridge) | PENDING V-1 |
| **22.3:** `CacheInterceptorTests` pass (fresh, stale, stale-while-revalidate, Vary) | PENDING V-1 |
| **22.4:** All `MockTransport` tests pass (capture, queue, delay, error) | PENDING V-1 |
| **22.4:** `MockTransport` preserves `OnRequestStart`-first ordering | PASS (static review) |
| **22.4:** Complete test suite passes (`dotnet test` clean run) | PENDING V-1 |

---

## Phase Gate

### Verdict: **CONDITIONALLY APPROVED**

Phase 22 is architecturally sound across all four sub-phases. The interceptor model correctly replaces the middleware pipeline with proper error delivery, clone-on-write semantics, and push-based handler callbacks. Three blockers require targeted fixes:

1. **P6-B1:** Suppress faults in `AwaitPendingMutationAsync` (cache read path must not abort on write failures)
2. **P6-B2:** Wrap `ThrowsAsync` delegate in `Task.Run` (prevent Unity SynchronizationContext deadlock)
3. **P7-B1:** Add handler callback exception safety wrapper (prevent transport corruption from handler throws)

Priority HIGH fixes from Pass 7:
4. **P7-H4:** Ensure inner handler gets terminal callback on redirect dispatch exception
5. **P7-H5:** Swap `TrySetException`/`DisposeBufferedState` order in `ResponseCollectorHandler.OnResponseError`
6. **P7-H2:** Restore context request on success path in `DecompressionInterceptor`
7. **P7-M2, P7-M4:** Address double-dispose risks in `CacheStoringHandler` and `DecompressionHandler`

Do not treat Phase 22 as fully validated until:
1. All 3 blockers are fixed and verified
2. HIGH findings P7-H4 and P7-H5 are addressed
3. Runtime/editor tests are executed in Unity Test Runner (V-1)
4. IL2CPP/mobile validation is completed (V-2)
5. The deferred decompression streaming follow-up (H-4) is tracked in the next relevant phase
