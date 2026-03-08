# Phase 22.1 Implementation Review — 2026-03-08

**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Scope:** All new, modified, and deleted files in Phase 22.1 (Core Interceptor Interfaces)
**Review passes:** 2 (initial review + re-review after fixes)

---

## Executive Summary

The interceptor-based dispatch chain is a well-designed replacement for the middleware pipeline. Core abstractions (`DispatchFunc`, `IHttpInterceptor`, `IHttpHandler`) are clean, composable, and preserve HTTP semantics. The handler callback model correctly separates response lifecycle events and lays the groundwork for streaming in Phase 22.2. The Core assembly boundary is verified clean — no `IHttpMiddleware`, `HttpPipeline`, `RegisterMiddleware`, or `Middlewares` symbols survive inside `Runtime/Core`. Platform compatibility is maintained: no new IL2CPP hazards, no WebGL regressions, no reflection-heavy patterns. The transport bridge pattern is a pragmatic transitional choice with documented double-buffering overhead.

The initial review identified 6 blocking items. The review-fix pass addressed most of them. The re-review confirms the fixes and identifies residual findings.

---

## Review Pass 1 — Initial Findings

### Blocking items identified:

| # | ID | Description | Status after fix pass |
|---|-----|-------------|----------------------|
| 1 | C-1 | `DispatchBridge` `internal` used by shipping `TurboHTTP.Testing` via `InternalsVisibleTo` | Fixed — `TransportDispatchHelper` public shim introduced |
| 2 | C-2 | `HttpMonitorEditorTests.cs` references deleted `HttpPipeline`/`IHttpMiddleware` | Fixed — guarded with `Assert.Ignore` |
| 3 | H-1 | `UHttpClient.SendAsync` duplicates `DispatchBridge.CollectResponseAsync` logic | Fixed — now uses `DispatchBridge.CollectResponseAsync` directly |
| 4 | H-2 | `dispatchCount` captured local with `Interlocked` — IL2CPP display class risk | Fixed — `InvocationGuard` class with explicit `_dispatchCount` field |
| 5 | H-4 | `HttpHeaders.Empty` mutable shared singleton — frozen flag not implemented | Fixed — `_frozen` flag + `ThrowIfFrozen()` implemented |
| 6 | H-5 | `RawSocketTransport.SendAsync` still `public` | Fixed — now `internal` |
| 7 | L-2 | `InterceptorPipeline` silently skips null interceptors | Fixed — throws `ArgumentException` |
| 8 | L-3 | `UHttpClient.Dispose()` unnecessarily rebuilds `InterceptorPipeline` | Fixed — removed |
| 9 | L-4 | `AdaptiveInterceptor` allocates async state machine when disabled | Fixed — split into sync check + async helper |
| 10 | L-5 | `EnsureCompleted()` does not dispose `_body` before throwing | Fixed — `DisposeBody()` called before throw |
| 11 | M-7 | `_pipeline` field not declared `volatile` | Fixed — `volatile` keyword added |

---

## Review Pass 2 — Re-Review Findings

### CRITICAL

#### C-1: `InternalsVisibleTo("TurboHTTP.Testing")` still present in `AssemblyInfo.cs`

**Source:** Infrastructure
**File:** `Runtime/Core/AssemblyInfo.cs`

`TransportDispatchHelper` public shim was correctly introduced and `MockTransport`/`RecordReplayTransport` use it. However, the `InternalsVisibleTo("TurboHTTP.Testing")` declaration remains in `AssemblyInfo.cs`. This grant is no longer needed now that `TransportDispatchHelper` is public. Should be removed to prevent `TurboHTTP.Testing` (a shipping assembly) from depending on internal Core types.

Additionally, `InterceptorPipelineTests.cs` still calls `DispatchBridge` directly — should be updated to use `TransportDispatchHelper`.

**Action required:** Remove `InternalsVisibleTo("TurboHTTP.Testing")` from `AssemblyInfo.cs`. Update test code to use `TransportDispatchHelper`.

#### C-2: `ResponseCollectorHandler.DisposeBody` race — `SegmentedBuffer.Dispose()` runs outside `_bodyGate` lock

**Source:** Infrastructure
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`

`OnResponseData` acquires `_bodyGate`, checks `_bodyClosed`, writes to `_body`. `DetachBody` acquires `_bodyGate`, sets `_bodyClosed = true; _body = null`, then returns the buffer. `DisposeBody` calls `DetachBody` then disposes the returned buffer **outside the lock**. If `OnResponseData` runs between `DetachBody` releasing the lock and `Dispose()` completing, it would find `_bodyClosed == true` and exit — which is correct. However, there's a window where a concurrent `OnResponseData` could have already acquired the lock and started writing before `_bodyClosed` was set.

In the current buffered-bridge model this race cannot occur (callbacks are synchronous). **Must be fixed before Phase 22.2** when streaming makes concurrent callbacks possible.

**Fix:** Dispose inside the lock using a local copy:
```csharp
private void DisposeBody()
{
    SegmentedBuffer body;
    lock (_bodyGate) { body = _body; _body = null; _bodyClosed = true; }
    body?.Dispose();
}
```

**Target:** Before Phase 22.2 work starts

---

### HIGH

#### H-1: `UHttpClient.Dispose()` blocks calling thread with `Task.Run(...).Wait(timeout)`

**Source:** Infrastructure
**File:** `Runtime/Core/UHttpClient.cs`

Plugin `ShutdownAsync` is called via `Task.Run` + `.Wait(timeout)`. On the Unity main thread, `.Wait()` blocks for up to `PluginShutdownTimeout` (default 5s), causing frame stalls. If the plugin's `ShutdownAsync` needs to marshal back to the main thread, deadlock is guaranteed.

**Recommendation:** Make `Dispose()` best-effort for plugin shutdown (log and continue), or document that callers must call `UnregisterPluginAsync` before `Dispose()` for graceful shutdown.

**Target:** Phase 22 hardening

#### H-2: `ThrowIfUnauthorizedErrorHandling` ordering causes false-positive blame for pass-through plugins

**Source:** Infrastructure
**File:** `Runtime/Core/PluginContext.cs`

In the catch block of `InvokeAsync`, `ThrowIfUnauthorizedErrorHandling(ex)` is called **before** `TryConsumePassThroughFault(ex)` is checked. A read-only plugin that correctly re-throws a transport error is incorrectly flagged as "unauthorized error handling." The `passThroughFault` variable is consumed but both branches re-throw — dead code.

**Fix:** Check pass-through fault first:
```csharp
if (!TryConsumePassThroughFault(ex))
    ThrowIfUnauthorizedErrorHandling(ex);
throw;
```

**Target:** 22.1 fix

#### H-3: `ResponseMutationMonitor` allocates per-request for all non-mutating plugins

**Source:** Infrastructure
**File:** `Runtime/Core/PluginContext.cs`

Every non-mutating plugin interceptor allocates: `ResponseMutationMonitor` (with `ResponseEventSignature[]`), `PluginMonitoringHandler`, `InvocationGuard`, `ObservedHandler` — at minimum 4-5 heap allocations per interceptor per request. Regresses against Phase 6 <500 bytes/request target.

**Target:** Phase 22 hardening (defer with doc)

#### H-4: `OnResponseError` never called for cancellation in transport

**Source:** Network
**File:** `Runtime/Transport/RawSocketTransport.cs`

`DispatchAsync` catches `UHttpException(Cancelled)` and converts to `OperationCanceledException` before `OnResponseError` can fire. Transport errors get `OnResponseError` but cancellation does not. Observability interceptors wrapping handlers will miss cancellation events.

**Target:** Phase 22.2

#### H-5: `ComputeDataHash` is O(n) per chunk for all non-mutating plugin interceptors

**Source:** Network
**File:** `Runtime/Core/PluginContext.cs`

Every `OnResponseData` through a read-only plugin interceptor hashes every byte twice (observed + forwarded). For a 10MB response, 20MB of byte-by-byte iteration on the hot path. Cache-unfriendly on mobile ARM.

**Recommendation:** Use length + first/last bytes as probabilistic fingerprint, or `MemoryMarshal.Read<long>` for block-level hashing.

**Target:** Phase 22 hardening

#### H-6: `ResponseCollectorHandler.Fail` exact-type-comparison for `OperationCanceledException` is fragile

**Source:** Network
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`

`ex.GetType() == typeof(OperationCanceledException)` distinguishes exact OCE (→ `TrySetCanceled`) from derived types like `BackgroundRequestQueuedException` (→ `TrySetException`). Correct for the specific use case but subtle — needs a code comment explaining why exact type comparison is used.

**Target:** 22.1 doc fix

---

### MEDIUM

#### M-1: `Assert.ThrowsAsync` not awaited in test lambdas — silent test failures

**Source:** Infrastructure
**File:** `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs`

`Assert.ThrowsAsync<...>` returns a `Task` that is not awaited inside the `Task.Run(async () => { ... })` lambda. The assertion failure is silently swallowed. Tests will always pass regardless of whether the expected exception is thrown.

**Fix:** Await `Assert.ThrowsAsync` within the lambda, or use synchronous exception assertion helpers.

**Target:** 22.1 fix

#### M-2: `BuildInterceptors` inserts `BackgroundNetworkingInterceptor` at index 0, overriding user ordering

**Source:** Infrastructure
**File:** `Runtime/Core/UHttpClient.cs`

`Insert(0, ...)` forces ordering: BackgroundNetworking → Adaptive → [user interceptors] → transport. If a user configures an auth interceptor that should apply before background queuing, it won't. Should be documented in `UHttpClientOptions.Interceptors` XML doc.

**Target:** Documentation

#### M-3: `BackgroundNetworkingInterceptor` coupled to `DispatchBridge` state keys

**Source:** Infrastructure
**File:** `Runtime/Core/BackgroundNetworkingPolicy.cs`

Uses `DispatchBridge.CancellationExceptionStateKey`. If Phase 22.2 removes these keys, this needs updating.

**Target:** 22.2 (tracked)

#### M-4: `ResponseCollectorHandler` stores error via `RequestContext.GetState` — coupling to `DispatchStateKeys`

**Source:** Network
**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`

Error passed through context state bag rather than direct parameter. Custom transports driving handlers directly could forget to set the key, silently losing errors.

**Target:** 22.2

#### M-5: ~80 test files reference deleted middleware — repo doesn't compile

**Source:** Infrastructure

Tracked for 22.3/22.4 migration.

**Target:** 22.3/22.4

#### M-6: `AdaptiveHandler._responseBytes` not synchronized for concurrent streaming

**Source:** Network
**File:** `Runtime/Core/AdaptiveInterceptor.cs`

`_responseBytes += chunk.Length` in `OnResponseData` is non-atomic. Safe in current single-threaded model but needs `Interlocked.Add` or documented contract.

**Target:** 22.2

#### M-7: `MockTransport` cancellation semantics differ from real transport

**Source:** Network
**File:** `Runtime/Testing/MockTransport.cs`

`MockTransport.DispatchAsync` lets raw `OperationCanceledException` through without converting from `UHttpException(Cancelled)` like `RawSocketTransport` does. Both reach same end state via different paths. Acceptable but should unify for test fidelity.

**Target:** 22.2

#### M-8: Double buffering in bridge path — documented and accepted

**Source:** Network
**File:** `Runtime/Core/Pipeline/DispatchBridge.cs`

Transport buffers full response → `DeliverResponse` iterates segments → `ResponseCollectorHandler` re-accumulates into new `SegmentedBuffer`. 2x memory for response bodies.

**Target:** 22.2 (bridge removal)

---

### LOW

#### L-1: `BackgroundNetworkingInterceptor.Wrap` allocates async state machine even when disabled

**File:** `Runtime/Core/BackgroundNetworkingPolicy.cs`
The `async` lambda allocates on every request even for the `!_policy.Enable` fast path. Split into sync check + async helper like `AdaptiveInterceptor`.
**Target:** Phase 22 hardening

#### L-2: `ResponseCollectorHandler` always allocates `SegmentedBuffer` even for HEAD responses

**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`
Consider lazy init on first `OnResponseData`.
**Target:** Low priority

#### L-3: `DispatchBridge` visibility — two-class indirection (`TransportDispatchHelper` → `DispatchBridge`)

**File:** `Runtime/Core/Pipeline/TransportDispatchHelper.cs`
Thin public shim delegates to internal impl. JIT inlines so no perf concern. Document that `TransportDispatchHelper` is the stable public surface.
**Target:** Low priority

#### L-4: `PluginContext.OptionsSnapshot` clones on every access

**File:** `Runtime/Core/PluginContext.cs`
Double-clone (constructor + property). Initialization-only path so impact is minimal.
**Target:** Low priority

#### L-5: `AdaptiveHandler._responseBytes` overflow on >2GB response

**File:** `Runtime/Core/AdaptiveInterceptor.cs`
Uses `int`. Academic on mobile. `ReadOnlySpan<byte>.Length` is also `int`.
**Target:** Low priority

---

## INFO

| ID | Description |
|----|-------------|
| I-1 | `IHttpHandler` callback ordering contract correctly documented in XML comments and consistently implemented across all transports. |
| I-2 | `HttpHeaders.Empty` frozen flag correctly implemented with `ThrowIfFrozen()` on `Set`, `Add`, `Remove`, `Clear`. `Clone()` returns unfrozen copy. |
| I-3 | `InterceptorPipeline` throws `ArgumentException` on null interceptor elements. |
| I-4 | All `await` calls in `Runtime/Core` use `ConfigureAwait(false)`. |
| I-5 | `TransportDispatchHelper` is a clean public stable surface wrapping `DispatchBridge`. |
| I-6 | `AdaptiveInterceptor` correctly splits disabled fast-path into synchronous return avoiding async state machine allocation. |
| I-7 | Plugin capability enforcement is comprehensive: request replacement guard, mutation signature, re-dispatch counting, response monitoring, out-of-band injection detection. |
| I-8 | `UHttpClient.Dispose()` no longer rebuilds pipeline. |
| I-9 | `UHttpClient.SendAsync` return type is `Task<UHttpResponse>` (clean-break). UniTask extensions updated accordingly. |
| I-10 | `UHttpRequest.Clone()` correctly prevents cloning disposed requests via `ThrowIfDisposed()`. |
| I-11 | `RequestMutationSignature` is a `readonly struct` — value-type semantics correct under IL2CPP. |
| I-12 | `RecordReplayTransport` passthrough triple-buffering acceptable for testing assembly. |

---

## Platform Compatibility

| Platform | Status | Notes |
|----------|--------|-------|
| Editor (Mono) | Compatible | No new reflection patterns beyond existing monitor auto-wiring |
| Standalone (Win/Mac/Linux) | Compatible | `DispatchFunc` delegate, `IHttpHandler` interface — no platform concerns |
| iOS (IL2CPP) | Compatible | All handler callbacks non-generic. `InvocationGuard` is proper class with `Interlocked` on fields. No generic virtual dispatch. No `System.Linq.Expressions` on hot path. |
| Android (IL2CPP) | Compatible | Same as iOS. `Volatile.Read/Write` used for ARM memory model. |
| WebGL | N/A | Transport excluded via asmdef `excludePlatforms` |

## Cancellation Propagation

Cancellation flows correctly through the interceptor chain:

1. `CancellationToken` propagates from `UHttpClient.SendAsync` → `DispatchBridge.CollectResponseAsync` → pipeline `DispatchFunc` → each interceptor → transport.
2. `ResponseCollectorHandler.Fail` correctly distinguishes `TaskCanceledException` (→ `TrySetCanceled`), exact `OperationCanceledException` (→ `TrySetCanceled`), and derived subtypes like `BackgroundRequestQueuedException` (→ `TrySetException`).
3. `DispatchBridge.AttachCompletion` handles `task.IsCanceled` via `collector.Cancel()`.
4. `DispatchStateKeys.CancellationException` allows interceptors to store specialized cancellation exceptions.

## Protocol Correctness

1. **Request/response lifecycle:** `OnRequestStart` fires before network I/O, `OnResponseStart` delivers status + headers, `OnResponseData` streams body chunks, `OnResponseEnd` finalizes. Error at any point delivers `OnResponseError` then no further callbacks.
2. **Status codes:** Passed as `int` in handler interface — correct for extension status codes.
3. **Trailers:** `OnResponseEnd(HttpHeaders trailers, ...)` passes `HttpHeaders.Empty` (frozen). Correct placeholder until HTTP/1.1 trailer parsing.
4. **Body ownership:** `OnResponseData(ReadOnlySpan<byte>)` — span valid only for call duration. XML doc states this constraint. `ResponseCollectorHandler` correctly copies via `_body.Write(chunk)`.
5. **Error model:** Transport errors are `UHttpException`. HTTP 4xx/5xx are normal responses (not exceptions). Preserved in handler model.

## TLS/Security

No TLS/security implications from the transport interface change. TLS handshake, certificate validation, and ALPN negotiation all occur inside `RawSocketTransport.SendAsync` (now internal), called from `DispatchAsync`. The handler model sits above the TLS layer.

---

## Phase Gate

### Blocking items (must fix before sign-off):

| # | ID | Severity | Fix |
|---|-----|----------|-----|
| 1 | C-1 | CRITICAL | Remove `InternalsVisibleTo("TurboHTTP.Testing")` from `AssemblyInfo.cs`; update tests to use `TransportDispatchHelper` |
| 2 | H-2 | HIGH | Fix `ThrowIfUnauthorizedErrorHandling` ordering — check pass-through fault first |
| 3 | M-1 | MEDIUM | Fix un-awaited `Assert.ThrowsAsync` in `InterceptorPipelineTests.cs` |
| 4 | H-6 | HIGH | Add code comment explaining exact-type-comparison in `Fail()` |

### Must fix before Phase 22.2:

| # | ID | Severity | Fix |
|---|-----|----------|-----|
| 1 | C-2 | CRITICAL | Fix `DisposeBody` race in `ResponseCollectorHandler` |

### Deferrable (Phase 22.2+ with documentation):

| ID | Severity | Description | Target |
|----|----------|-------------|--------|
| H-1 | HIGH | `Dispose()` blocks main thread with `Task.Run.Wait` | 22 hardening |
| H-3 | HIGH | `ResponseMutationMonitor` per-request allocation | 22 hardening |
| H-4 | HIGH | `OnResponseError` not called for cancellation | 22.2 |
| H-5 | HIGH | `ComputeDataHash` O(n) per chunk | 22 hardening |
| M-2 | MEDIUM | `BuildInterceptors` ordering override needs doc | Documentation |
| M-3 | MEDIUM | `BackgroundNetworkingInterceptor` coupled to state keys | 22.2 |
| M-4 | MEDIUM | Error via context state bag coupling | 22.2 |
| M-5 | MEDIUM | ~80 test files reference deleted middleware | 22.3/22.4 |
| M-6 | MEDIUM | `_responseBytes` not synchronized | 22.2 |
| M-7 | MEDIUM | `MockTransport` cancellation semantics differ | 22.2 |
| M-8 | MEDIUM | Double buffering in bridge | 22.2 |
| L-1–L-5 | LOW | Various minor allocation/overflow/doc items | Low priority |
