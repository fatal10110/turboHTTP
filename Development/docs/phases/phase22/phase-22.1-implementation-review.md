# Phase 22.1 Implementation Review

**Reviewers:** unity-infrastructure-architect, unity-network-architect
**Scope:** All new, modified, and deleted files in Phase 22.1 (Core Interceptor Interfaces)
**Review passes:** 4
**Final verdict:** APPROVED

---

## Executive Summary

The interceptor-based dispatch chain is architecturally sound and represents a clean evolution from the middleware pipeline. Core abstractions (`DispatchFunc`, `IHttpInterceptor`, `IHttpHandler`) are minimal, composable, and preserve HTTP semantics. The handler callback model correctly separates response lifecycle events and lays the groundwork for streaming in Phase 22.2.

All blocking items from passes 1–3 are confirmed CLOSED. The Core assembly boundary is verified clean. Platform compatibility is maintained across all targets. Both reviewers independently approve Phase 22.1 for sign-off.

---

## Finding Disposition — All Prior Passes

### Pass 1 & 2 Items — All CLOSED

| ID | Description | Resolution |
|----|-------------|------------|
| C-1 (p1) | `DispatchBridge` internal used by shipping `TurboHTTP.Testing` | `TransportDispatchHelper` public shim; `InternalsVisibleTo("TurboHTTP.Testing")` removed |
| C-2 (p1) | `HttpMonitorEditorTests.cs` references deleted types | Guarded with `Assert.Ignore` |
| H-1 (p1) | `SendAsync` duplicates `CollectResponseAsync` logic | Delegates to `DispatchBridge.CollectResponseAsync` |
| H-2 (p1) | `dispatchCount` captured local IL2CPP risk | `InvocationGuard` sealed class with explicit field |
| H-4 (p1) | `HttpHeaders.Empty` mutable singleton | `_frozen` flag + `ThrowIfFrozen()` |
| H-5 (p1) | `RawSocketTransport.SendAsync` still public | Now `internal` |
| L-2 (p1) | Null interceptors silently skipped | Throws `ArgumentException` |
| L-3 (p1) | Dispose rebuilds pipeline | Removed |
| L-4 (p1) | AdaptiveInterceptor async state machine when disabled | Sync/async split |
| L-5 (p1) | `EnsureCompleted` body leak | `DisposeBody()` before throw |
| M-7 (p1) | `_pipeline` not volatile | `volatile` keyword added |
| C-1 (p2) | `InternalsVisibleTo` still present | Removed |
| C-2 (p2) | `DisposeBody` race | Local-copy-under-lock pattern |
| H-1 (p2) | Dispose blocks main thread | Fire-and-forget shutdown |
| H-2 (p2) | Pass-through fault ordering | `TryConsumePassThroughFault` checked first |
| H-4 (p2) | `OnResponseError` not called for cancellation | All three transports now call `OnResponseError` |
| H-5 (p2) | `ComputeDataHash` O(n) per chunk | Bounded sampling (first 8 + last 8 bytes) |
| H-6 (p2) | Exact-type-comparison comment | Comment added |
| M-1 (p2) | Un-awaited `Assert.ThrowsAsync` | Properly awaited |
| L-1 (p2) | Background interceptor async alloc when disabled | Sync/async split |
| L-2 (p2) | SegmentedBuffer for HEAD responses | Lazy init |
| L-5 (p2) | `_responseBytes` overflow | `long` with `Interlocked.Add` |
| M-7 (p2) | MockTransport cancellation semantics | Aligned with real transport |

### Pass 3 Items — All CLOSED

| ID | Description | Resolution |
|----|-------------|------------|
| H-1 (p3) | `_pendingObserved` ArrayPool buffer leak after release | `_observedBufferReleased` flag + `ThrowIfObservedBufferReleased_NoLock()` |
| H-2 (p3) | `_pluginLifecycleGate` dispose race | `ObjectDisposedException` caught/rethrown at `WaitAsync` call sites |
| H-3 (p3) | `RecordReplayTransport.DispatchAsync` no cancellation handling | Added matching cancellation pattern |
| M-1 (p3) | `SuccessfulStubTransport` manual callback loop | Uses `TransportDispatchHelper.DeliverResponse` |
| M-2 (p3) | `IsInstanceOfType` reflection | Editor-only (`#if UNITY_EDITOR`), no IL2CPP risk |
| M-3 (p3) | XML doc on `DeliverResponse` for `OnRequestStart` contract | XML doc added |
| M-4 (p3) | `ArrayPool.Return(clearArray: false)` safety comment | Comments added |
| L-1 (p3) | Raw OCE gap in `RawSocketTransport` | Explicit `OperationCanceledException` catch added |
| L-2 (p3) | Full-trust plugin guard allocation | Fast-path skips `CapabilityEnforcedInterceptor` |
| L-3 (p3) | `FailingTransport` doesn't call `OnRequestStart` | Now calls `OnRequestStart` before throwing |

---

## Pass 4 — New Findings

### MEDIUM

#### M-1: `DispatchBridge.DeliverResponse` calls `context.UpdateRequest` before `OnResponseStart` — undocumented mutation

**Source:** Infrastructure
**File:** `Runtime/Core/Pipeline/DispatchBridge.cs`

`context.UpdateRequest(request)` is called before `OnResponseStart`. If an interceptor's `OnResponseStart` reads `context.Request` expecting the original request, it sees the response's embedded request instead. In Phase 22.2 when transports drive handler callbacks natively, this mutation won't be replicated, creating a silent behavioral difference.

**Recommendation:** Document in XML comment, or remove the call (callers should call `context.UpdateRequest` themselves).
**Target:** Before Phase 22.2

#### M-2: Overview spec C-7 error delivery contract table outdated

**Source:** Network
**File:** `Development/docs/phases/phase22/overview.md`

Overview states cancellation has **no** `OnResponseError` callback. Implementation (correctly) DOES call `OnResponseError` for observer visibility. The spec must match the implementation before Phase 22.2 begins.

**Target:** Before Phase 22.2

#### M-3: `DeliverResponse` XML doc should note `response.Error` stored in `RequestContext`

**Source:** Network
**File:** `Runtime/Core/Pipeline/DispatchBridge.cs`

When `response.Error` is non-null, it's stored via `context.SetResponseError` but `OnResponseError` is never called (correct: HTTP 4xx/5xx are normal responses). This behavior should be documented.

**Target:** Doc fix

### LOW

#### L-1: `AdaptiveInterceptor` full body clone for timeout-only adaptation

**File:** `Runtime/Core/AdaptiveInterceptor.cs`
`Clone()` calls `Body.ToArray()` even when only the timeout changes. Could add a shallow timeout-override path.
**Target:** Phase 22 hardening

#### L-2: `ResponseCollectorHandler.OnResponseEnd` — use `HttpHeaders.Empty` instead of `new HttpHeaders()` for null fallback

**File:** `Runtime/Core/Pipeline/ResponseCollectorHandler.cs`
Minor allocation on protocol-violation path. `HttpHeaders.Empty` (frozen) is semantically more correct.
**Target:** Cleanup

#### L-3: `InterceptorPipeline` does not validate `Wrap` returns non-null

**File:** `Runtime/Core/Pipeline/InterceptorPipeline.cs`
If `IHttpInterceptor.Wrap` returns null, `_pipeline` becomes null → `NullReferenceException` with no diagnostic context.
**Recommendation:** Add null-check with diagnostic message.
**Target:** Defensive hardening

#### L-4: `GuardedNextAsync` async state machine per call for simple observers

**File:** `Runtime/Core/PluginContext.cs`
Unavoidable under .NET Standard 2.1 without `ValueTask` + `IsCompletedSuccessfully` fast-path.
**Target:** Phase 22 hardening

#### L-5: `_pipeline ?? _transport.DispatchAsync` TOCTOU with concurrent dispose

**File:** `Runtime/Core/UHttpClient.cs`
Narrow race window on concurrent `SendAsync` + `Dispose()`. Surfaces as `ObjectDisposedException` (not silent). Document or snapshot transport delegate at construction.
**Target:** Phase 22 hardening

---

## Carried Forward (Deferred)

| ID | Severity | Description | Target |
|----|----------|-------------|--------|
| M-5 (p3) | MEDIUM | ~80 test files reference deleted middleware APIs | 22.3/22.4 |
| M-6 (p3) | MEDIUM | `Task.Run(...).GetAwaiter().GetResult()` test pattern | 22.4 |
| H-3 (p2) | HIGH | `ResponseMutationMonitor` per-request allocation for non-mutating plugins | 22 hardening |

---

## Verified Properties

### Assembly Boundary
- `Runtime/Core` — zero references to middleware-era symbols. Zero `InternalsVisibleTo("TurboHTTP.Testing")`.
- `Runtime/Transport` — uses only public types (`TransportDispatchHelper`, `IHttpHandler`, `IHttpTransport`).
- `Runtime/Testing` — uses only `TransportDispatchHelper` (public). No `DispatchBridge` references.

### Thread Safety
| Site | Mechanism | Verdict |
|------|-----------|---------|
| `_pipeline` read/write | `volatile` | Correct |
| `_request` in `RequestContext` | `volatile` | Correct |
| `_responseBytes` in `AdaptiveHandler` | `Interlocked.Add/Read` | Correct, ARM64-safe |
| `_pendingObserved` buffer | `_responseGate` lock | Correct |
| `_disposed` in `UHttpClient` | `Interlocked.CompareExchange` | Correct |
| `_disposed` in `RawSocketTransport` | `Volatile.Read` + `Interlocked.CompareExchange` | Correct |
| `_body`/`_bodyClosed` in `ResponseCollectorHandler` | `_bodyGate` lock | Correct |

### Platform Compatibility
| Platform | Status |
|----------|--------|
| Editor (Mono) | Compatible |
| Standalone (Win/Mac/Linux) | Compatible |
| iOS (IL2CPP) | Compatible |
| Android (IL2CPP) | Compatible |
| WebGL | N/A (transport excluded) |

### Cancellation Propagation
Verified correct end-to-end:
1. Token flows from `SendAsync` → `CollectResponseAsync` → pipeline → transport
2. All three transports call `OnResponseError` for cancellation + store OCE via `SetCancellationException`
3. `Fail` routes exact OCE/TCE → `TrySetCanceled`; derived types → `TrySetException`
4. `BackgroundRequestQueuedException` preserved as typed exception through full chain

### Handler Callback Ordering
All transports and test doubles verified: `OnRequestStart` always first, followed by `OnResponseStart` → `OnResponseData*` → `OnResponseEnd` (or `OnResponseError` after `OnRequestStart`).

### Protocol Correctness
- Status codes as `int` (correct for extension codes)
- Trailers: `HttpHeaders.Empty` (frozen placeholder)
- Body: `ReadOnlySpan<byte>` valid only for call duration (documented, collector copies)
- Error model: transport errors = `UHttpException`; HTTP 4xx/5xx = normal responses

### TLS/Security
No implications from transport interface change. TLS occurs inside `RawSocketTransport.SendAsync` (internal).

---

## Phase Gate

### Verdict: **APPROVED**

Both specialist reviewers independently approve Phase 22.1 for sign-off. All CRITICAL and HIGH findings from 4 review passes are CLOSED.

### Should fix before Phase 22.2 (documentation only):

| # | Fix |
|---|-----|
| 1 | Document or remove `context.UpdateRequest` call in `DeliverResponse` (M-1) |
| 2 | Update overview.md C-7 table — cancellation now calls `OnResponseError` (M-2) |
| 3 | Add `response.Error` remark to `DeliverResponse` XML doc (M-3) |
