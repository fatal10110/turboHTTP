# Phase 22 Review — 2026-03-08

**Reviewer:** Antigravity  
**Documents reviewed:** `overview.md`, `phase-22.1-core-interfaces.md`, `phase-22.2-transport-adaptation.md`, `phase-22.3-interceptor-rewrites.md`, `phase-22.4-testing-adaptation.md`  
**Cross-referenced source files:** `IHttpTransport.cs`, `IHttpMiddleware.cs`, `HttpPipeline.cs`, `UHttpClient.cs`, `UHttpRequest.cs`, `RequestContext.cs`, `PluginContext.cs`, `RetryMiddleware.cs`, `RedirectMiddleware.cs`, `Http2Stream.cs`, `Http2StreamPool.cs`, `Http2Connection.cs`, `PoolableValueTaskSource.cs`, `SegmentedBuffer.cs`, `BackgroundNetworkingPolicy.cs`

---

## Overall Assessment

The Phase 22 plan is **thorough, well-structured, and implementation-ready**. The undici-style interceptor model is a strong architectural choice that cleanly solves the two structural limitations (fully-buffered bodies and no short-circuit/re-dispatch). The error delivery contract is clear and eliminates the dual-path ambiguity of the current model. The sub-phase decomposition is logical and the validation criteria are specific.

---

## Findings

### Critical

#### C-18: Transport `DisposeBodyOwner` breaks Retry/Redirect (Use-After-Free)

Phase 22.2 explicitly preserves `request.DisposeBodyOwner()` inside the `finally` block of `RawSocketTransport.DispatchAsync`. Because Phase 19c transitioned `UHttpRequest.Body` to be backed by a pooled `IMemoryOwner<byte>`, disposing this owner returns the array to the pool. When an interceptor (like `RetryInterceptor` or `RedirectInterceptor` on 307/308) re-dispatches the same request object, the transport will attempt to send a body backed by a disposed memory owner, causing array pool corruption or an `ObjectDisposedException`.

**Impact:** `RetryInterceptor` will crash or send garbage data on attempt 2+.

> [!CAUTION]
> **Recommendation:** Remove `DisposeBodyOwner()` from the transport entirely. Request body disposal must be tied exclusively to the lifecycle of the request itself (handled when the request returns to the pool via `ResetForPool`) or the outermost pipeline executor.

---

### High

#### H-11: Missing API prerequisites — `UHttpRequest.Clone()`, `WithTimeoutInternal()`, `RequestContext.CreateForBackground()`

The plan references three APIs that **do not exist** in the current codebase:

| API | Referenced by | Current state |
|-----|---------------|---------------|
| `request.Clone()` | `CacheInterceptor` (22.3, line 225) | ❌ Not present on `UHttpRequest` |
| `req.WithTimeoutInternal()` | `AdaptiveInterceptor` (22.3, line 61) | ❌ Not present; only `WithHeader()` exists |
| `RequestContext.CreateForBackground()` | `CacheInterceptor` (22.3, line 226) | ❌ Not present on `RequestContext` |

**Impact:** These are blocking prerequisites for 22.3. If they're planned as part of Phase 22 implementation (created inline), this needs to be explicitly stated in 22.1 or the overview's "Files Changed Summary". If they're expected from Phase 23, that dependency should be documented.

> [!IMPORTANT]
> **Recommendation:** Add a "New API surface" section to 22.1 listing these three methods and their signatures, or clarify which prior phase delivers them.

#### H-12: Redirect re-dispatch conflicts with collector completion semantics

The new `UHttpClient.SendAsync` design in the overview attaches `collector.EnsureCompleted()` to the outer pipeline task, assuming that pipeline completion means the request lifecycle is fully terminal. The `RedirectHandler` design in 22.3 breaks that assumption by starting the follow-up dispatch fire-and-forget from `OnResponseEnd` and returning from the original dispatch before the redirected hop has finished.

**Impact:** The first hop can complete the outer pipeline task while the redirected hop is still in flight. That can fault the collector early with "Pipeline completed without delivering a response" and also lets dispatch-scoped wrappers such as `ConcurrencyInterceptor` release resources before the redirect chain is actually done.

> [!IMPORTANT]
> **Recommendation:** Redirection needs an explicit completion bridge that keeps the outer dispatch pending until the terminal hop completes, rather than relying on a fire-and-forget continuation.

#### H-13: `UHttpRequest.WithHeader` mutates in-place, violating "Clone before mutation" assumption

In 22.3, `AuthInterceptor` notes: `// Clone request/headers before mutation... req = req.WithHeader(...)`. However, `UHttpRequest.WithHeader` (and `WithTimeoutInternal`) mutates the request *in-place* and returns `this`, rather than returning a clone.

**Impact:** The original request is mutated. If a request is re-dispatched (e.g. by `RetryInterceptor` or a redirect), it will carry headers or timeout modifications from previous attempts that were meant to be transient, violating thread-safety and correctness.

> [!IMPORTANT]
> **Recommendation:** Ensure a true `Clone()` method is implemented (see H-11) and explicitly call it before mutating headers or timeout in interceptors that modify the request.

---

### Medium

#### M-12: `Http2Stream.Cancel()` — conflicting error delivery with ValueTask semantics

In 22.2, `Http2Stream.Cancel()` is specified as:
```csharp
_handler.OnResponseError(error, _context);
_completionSource.SetException(new OperationCanceledException());
```

This calls `OnResponseError` (error delivery) **and** faults the `ValueTask` with `OperationCanceledException`. Per the Error Delivery Contract (C-7 in the overview), cancellation should cause the Task to throw `OperationCanceledException` with **no** `OnResponseError` callback. The 22.2 spec itself has a note explaining why both fire, but it contradicts the contract table in the overview.

> [!WARNING]
> **Recommendation:** Decide which takes precedence. If `Http2Stream.Cancel()` represents a cancellation, it should follow the contract (no `OnResponseError`). If it sits below the contract boundary (transport-internal), the note should reference this explicitly and the transport's `DispatchAsync` catch block should be the contract enforcement point.

#### M-13: `RedirectHandler.OnResponseEnd` fire-and-forget — unobserved Task completion

The `RedirectHandler` dispatches the redirect hop via fire-and-forget with a `ContinueWith(NotOnRanToCompletion)` fault bridge. However, the `redirectTask` itself is never awaited. If the redirect dispatch completes successfully, the task is GC'd. This is correct for the described design, but `TaskScheduler.UnobservedTaskException` will fire if any unhandled exception leaks through the `ContinueWith` itself.

> [!NOTE]
> **Recommendation:** Document that the `ContinueWith` lambda must not throw internally (wrap in try-catch), or store the task reference to suppress the unobserved exception warning.

#### M-14: `CapabilityEnforcedInterceptor` request mutation check — timing issue

The post-call mutation check in 22.1 (line 182–184) captures the request signature **after** `_pluginInterceptor.Wrap(guarded)` completes. However, some interceptors (Auth, Adaptive) legitimately mutate the request via `req.WithHeader()` or `req.WithTimeoutInternal()` — these create **new** request objects rather than mutating in place. The mutation check would compare the original `req` signature, which hasn't changed. This is probably the intended behavior (checking in-place mutation of the original object), but the doc says "Compare `RequestMutationSignature` before/after" which is ambiguous.

> [!NOTE]
> **Recommendation:** Clarify that mutation detection targets in-place mutation of the original request object's headers/body, not legitimate cloning patterns.

#### M-15: Test file rename table is incomplete

Phase 22.4 lists test file renames but is missing several:
- `Tests/Runtime/Cache/CacheMiddlewareTests.cs` → `CacheInterceptorTests.cs`
- `Tests/Runtime/Observability/MonitorMiddlewareTests.cs` → `MonitorInterceptorTests.cs`
- `Tests/Runtime/Transport/AdaptiveMiddlewareTests.cs` → `AdaptiveInterceptorTests.cs`
- `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs` — currently lives under `Pipeline/`, should move to `Middleware/`?

> [!NOTE]
> **Recommendation:** Add the missing renames to the 22.4 table. Clarify target directory for `DefaultHeadersInterceptorTests.cs`.

#### M-16: Overview overstates streaming and memory wins delivered in Phase 22

The overview frames the redesign as enabling streaming chunks, meaningful progress callbacks, and in-flight decompression without a second buffer. The sub-phase docs are more constrained:

- 22.2 still buffers the full HTTP/1.1 response before any handler callback fires.
- 22.3 still buffers the full compressed body in `DecompressionHandler` until `OnResponseEnd`.

**Impact:** The interceptor architecture improves composition immediately, but the documented peak-memory reduction and progress semantics are not fully realized in this phase, especially for HTTP/1.1 and compressed responses.

> [!NOTE]
> **Recommendation:** Tighten the overview language to distinguish architectural enablement from the concrete behavior Phase 22 actually delivers.

#### M-17: `CapabilityEnforcedInterceptor` drops the current request-replacement guard

The proposed `CapabilityEnforcedInterceptor` checks request mutation by comparing signatures on the original request before and after plugin execution, and it counts re-dispatches. What it does **not** explicitly preserve from the current middleware guard is the prohibition on passing a different request instance to `next()` without request-mutation rights.

In the current `PluginContext` guard, this is enforced directly:

```csharp
if (!_canMutateRequest && !ReferenceEquals(nextRequest, request))
{
    throw new PluginException(
        _pluginName,
        "middleware.request",
        "Plugin middleware attempted request replacement without MutateRequests capability.");
}
```

**Impact:** Under the planned interceptor version, a plugin can construct a replacement request, call `next()` once, and evade the post-call signature check because the original request object was never mutated.

> [!WARNING]
> **Recommendation:** Preserve an explicit request-identity check in the wrapped `next` path in addition to the before/after signature comparison.

---

### Low

#### L-8: `ResponseCollectorHandler._body` initialized eagerly

`ResponseCollectorHandler` allocates `new SegmentedBuffer()` in its constructor (22.1, line 68). For cached responses that never reach the transport (cache hit path), the `ResponseCollectorHandler` is still created by `UHttpClient.SendAsync`, but `CacheInterceptor.ServeCachedEntry` drives callbacks against the handler that `CacheInterceptor` receives — which is the `ResponseCollectorHandler`. The `SegmentedBuffer` allocated in the collector would accumulate the cached body data, which is correct but creates a copy of already-cached data. This is the expected trade-off for the clean push model.

#### L-9: `BackgroundNetworkingInterceptor` — public API `TryDequeueReplayable` placement

The plan states `TryDequeueReplayable` stays on the interceptor (M-11), but currently it's on `BackgroundNetworkingMiddleware` in `BackgroundNetworkingPolicy.cs`. The rename section in 22.3 doesn't list `BackgroundNetworkingPolicy.cs` in the rename table (line 285 says "middleware part" extracted). Confirm whether the `TryDequeueReplayable` method stays on `BackgroundNetworkingPolicy` or moves to the new `BackgroundNetworkingInterceptor`.

#### L-10: `MockTransport.DriveHandler` — `OnRequestStart` after error check

In 22.4, `DriveHandler` (line 32–38) checks `r.Error != null` and calls `OnResponseError` before `OnRequestStart`. Per the `IHttpHandler` contract, `OnRequestStart` is "always the first callback" and `OnResponseError` "may be called at any point **after** `OnRequestStart`". This means `OnRequestStart` should fire even on error responses.

**Impact:** Test transports would exercise a callback ordering the production contract forbids, which can hide bugs in handler wrappers such as logging, metrics, retry, and redirect.

> [!NOTE]
> **Recommendation:** Move `handler.OnRequestStart(request, ctx)` before the error check in `DriveHandler`, or add a doc note explaining that `MockTransport` intentionally skips `OnRequestStart` for pre-error scenarios.

---

## Second Review — 2026-03-08

**Reviewer:** Claude Opus 4.6 (infrastructure + network architect)
**Scope:** Post-22.1-partial-implementation review — plan-vs-code divergence, platform, protocol

### High

#### H-19: `ResponseCollectorHandler` diverges from plan spec — transitional `DispatchBridge` scaffolding

The implemented `ResponseCollectorHandler` has logic not in the 22.1 plan: `TrySetStoredCancellationException()` reading from `DispatchBridge.CancellationExceptionStateKey`, and `DispatchBridge.ResponseErrorStateKey` used in `OnResponseEnd`. These are transitional bridge scaffolding needed while `RawSocketTransport` delegates to the legacy `SendAsync`.

**Fix:** Added "Transitional Bridge" section to 22.1 documenting `DispatchBridge` and its state keys. Added explicit removal instructions and validation gate to 22.2.

#### H-20: Double-buffering bridge in 22.1 → 22.2 transition

The current `RawSocketTransport.DispatchAsync` wraps the old `SendAsync` → `UHttpResponse` path and re-emits via `DispatchBridge.DeliverResponse`. The response body is double-buffered and an intermediate `UHttpResponse` is allocated and immediately disposed.

**Fix:** Added transitional bridge overhead note to overview Performance Impact section. Added bridge removal validation gate to 22.2 (`grep` check for zero `DeliverResponse`/state-key references).

#### H-21: `ReadOnlySequenceStream` adapter design missing

`DecompressionHandler` references a `ReadOnlySequenceStream` adapter with fallback "copy to `MemoryStream`". The fallback defeats LOH avoidance for compressed bodies >85KB. `ReadOnlySequenceStream` does not exist in .NET Standard 2.1.

**Fix:** Designed the `ReadOnlySequenceStream` adapter (~40 lines) in 22.3 spec, placed in `Runtime/Core/Internal/ReadOnlySequenceStream.cs`. Added to overview Files Changed Summary.

### Medium

#### M-18: `OnResponseData` span lifetime constraint underdocumented

`IHttpHandler.OnResponseData(ReadOnlySpan<byte>)` requires callers to copy data they wish to retain. This was specified in the plan but not in the actual `IHttpHandler.cs` source.

**Fix:** Added XML docs to `IHttpHandler.cs` source for all five methods, including the "span is valid only for the duration of this call" constraint.

#### M-19: `ConcurrencyInterceptor` semaphore held during redirect chains — undocumented

The concurrency permit remains held for the entire redirect chain. Correct behavior but not documented.

**Fix:** Added redirect chain note to `ConcurrencyInterceptor` spec in 22.3.

#### M-20: `RetryDetectorHandler.OnRequestStart` forwarding behavior unspecified

`RetryDetectorHandler` doesn't specify what `OnRequestStart` does. Per the `IHttpHandler` contract, `OnResponseError` may only be called after `OnRequestStart`. If `RetryDetectorHandler` doesn't forward `OnRequestStart`, inner handlers (Logging, Metrics) may not have initialized their state before receiving `OnResponseError`.

**Fix:** Specified that `RetryDetectorHandler.OnRequestStart` always forwards to `_inner` regardless of retry state.

#### M-21: `HttpHeaders.Empty` is a shared mutable singleton

`HttpHeaders.Empty` is `public static readonly` but `HttpHeaders` is fully mutable (`Set`, `Add`, `Remove`). Any handler that mutates the trailers object corrupts the singleton.

**Fix:** Added `_frozen` flag design to 22.1 spec: `Empty` is constructed with `frozen: true`, mutation methods call `ThrowIfFrozen()`. Updated overview Files Changed and Key Design Decisions.

#### M-22: Plan-vs-code divergence on `SendAsync` return type and 22.1 completion

Plan describes `SendAsync` return type change as future work, but code already returns `Task<UHttpResponse>`. Multiple 22.1 artifacts already exist in code.

**Fix:** Added "Implementation status" note to overview documenting partial 22.1 completion.

### Low

#### L-11: `NullHandler` scoped too narrowly as private inner class

Plan specifies `NullHandler` as private static inner class of `Http2Stream`. Other transport code may need a no-op handler.

**Fix:** Widened to `internal static` class at transport assembly level (`Runtime/Transport/NullHandler.cs`).

#### L-12: Decompression 64KB buffer not guarded by finally

The 64KB `ArrayPool<byte>` buffer used in the decompression loop must be returned on decompression errors (e.g., corrupt gzip data).

**Fix:** Added `finally` block requirement to decompression loop spec in 22.3.

#### L-13: `AdaptiveHandler._bytesReceived` type unspecified — overflow risk

For responses >2GB, `int` would overflow.

**Fix:** Specified as `long` in 22.3 spec.

---

## Summary Table

| Severity | ID | Title | Status |
|----------|----|-------|--------|
| Critical | C-18 | Transport `DisposeBodyOwner` breaks Retry/Redirect | ✅ Approved |
| High | H-11 | Missing API prerequisites | ✅ Approved |
| High | H-12 | Redirect re-dispatch conflicts with collector completion semantics | ✅ Approved |
| High | H-13 | In-place mutation violates cloning assumption | ✅ Approved |
| High | H-19 | `ResponseCollectorHandler` transitional `DispatchBridge` scaffolding | ✅ Fixed |
| High | H-20 | Double-buffering bridge in 22.1 → 22.2 transition | ✅ Fixed |
| High | H-21 | `ReadOnlySequenceStream` adapter design missing | ✅ Fixed |
| Medium | M-12 | `Http2Stream.Cancel()` error contract conflict | ✅ Approved |
| Medium | M-13 | `RedirectHandler` fire-and-forget unobserved Task | ✅ Approved |
| Medium | M-14 | `CapabilityEnforcedInterceptor` mutation check ambiguity | ✅ Approved |
| Medium | M-15 | Incomplete test rename table | ✅ Approved |
| Medium | M-16 | Overview overstates Phase 22 streaming gains | ✅ Approved |
| Medium | M-17 | `CapabilityEnforcedInterceptor` drops request-replacement guard | ✅ Approved |
| Medium | M-18 | `OnResponseData` span lifetime underdocumented | ✅ Fixed |
| Medium | M-19 | `ConcurrencyInterceptor` redirect chain semaphore hold | ✅ Fixed |
| Medium | M-20 | `RetryDetectorHandler.OnRequestStart` forwarding unspecified | ✅ Fixed |
| Medium | M-21 | `HttpHeaders.Empty` shared mutable singleton | ✅ Fixed |
| Medium | M-22 | Plan-vs-code divergence on 22.1 completion | ✅ Fixed |
| Low | L-8 | `ResponseCollectorHandler` eager SegmentedBuffer alloc | ✅ Acknowledged |
| Low | L-9 | `BackgroundNetworkingInterceptor` API placement | ✅ Approved |
| Low | L-10 | `MockTransport.DriveHandler` callback ordering | ✅ Approved |
| Low | L-11 | `NullHandler` scoped too narrowly | ✅ Fixed |
| Low | L-12 | Decompression buffer not guarded by finally | ✅ Fixed |
| Low | L-13 | `AdaptiveHandler._bytesReceived` overflow risk | ✅ Fixed |

---

## Approval

**Approved by:** Artur Koshtei
**Date:** 2026-03-08

All 12 original findings have been reviewed against the source code and sub-phase documents and are **approved**. Key verification notes:

- **C-18**: Confirmed — `DisposeBodyOwner()` is called in `finally` blocks across 4 locations in `RawSocketTransport.cs` and in `Http2Connection.cs`. Must be removed from the transport to avoid use-after-free on retry/redirect re-dispatch.
- **H-11**: Confirmed — `UHttpRequest.Clone()`, `WithTimeoutInternal()`, and `RequestContext.CreateForBackground()` do not exist. Note: `SetTimeoutInternal()` exists (line 281 of `UHttpRequest.cs`) but the plan references a fluent `WithTimeoutInternal()` variant that does not. These APIs must be created as part of 22.1 or explicitly tracked.
- **H-13**: Confirmed — `WithHeader()` (line 79–86 of `UHttpRequest.cs`) mutates `_headers` in-place and returns `this`. Not a clone.
- **M-17**: Confirmed — `PluginContext.cs` line 124 has the `ReferenceEquals(nextRequest, request)` guard that is absent from the `CapabilityEnforcedInterceptor` design.

All recommendations in the findings are accepted. The sub-phase documents should be updated to address each finding before implementation begins.

9 additional findings (H-19 through L-13) from the second review have been fixed directly in the plan documents and source code.
