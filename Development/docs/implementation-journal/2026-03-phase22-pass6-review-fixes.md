# Phase 22: Pass 6 Review Fixes

**Date:** 2026-03-15  
**Phase:** 22 full implementation review follow-up (Pass 6)  
**Status:** Both pass-6 blockers fixed, actionable warnings addressed in code/tests, Unity compile/device validation still pending

## What Was Fixed

This pass closes the implementation issues raised in `Development/docs/phases/phase22/phase-22-implementation-review.md`:

1. `CacheInterceptor.AwaitPendingMutationAsync(...)` no longer propagates background store/remove failures into the foreground cache lookup path. Reads still wait for sequencing, but faulted/canceled mutation tasks are now treated as best-effort background failures.
2. `AssertAsync.ThrowsAsync(...)` now mirrors `AssertAsync.Run(...)` by executing async delegates through `Task.Run(...)` before blocking. This removes the Unity Editor deadlock risk when awaited continuations post back to the current `SynchronizationContext`.
3. `IHttpHandler.OnResponseError(...)` XML docs were tightened to call out the partial-body callback sequence explicitly.
4. `RedirectHandler.CompleteWithEnd(...)` and `CompleteWithError(...)` no longer rethrow after faulting the completion source. The redirect bridge now has a single authoritative failure path.
5. `MonitorHandler` now caches the buffered-response capture limit once in `OnResponseStart(...)` instead of re-reading it on every response chunk.
6. `CacheStoringHandler.OnResponseEnd(...)` now treats synchronous cache-store queue failures as non-fatal for the already-delivered response. Detached body buffers are disposed and the queueing failure is logged to `Debug.WriteLine(...)`.
7. `RedirectInterceptor.CopyBodyForRedirect(...)` now always copies preserved request bodies so redirect hops never share the caller's backing array.
8. `MockTransport`'s fallback `UHttpResponse` path now snapshots response headers/body into owned data before disposing the source response, removing dependence on pooled response-body lifetime during callback delivery.
9. `RetryDetectorHandler` now documents the Phase 22 transport assumption behind suppressed 5xx body callbacks: HTTP/1.1 parsing/draining completes before handler callbacks are emitted, so retry detection does not strand unread bytes on keep-alive connections in the current buffered transport model.

## Tests Added / Updated

1. Added `Tests/Runtime/AssertAsyncTests.cs` to cover the test-thread deadlock regression for both `Task` and `ValueTask` `ThrowsAsync(...)` overloads under a non-pumping `SynchronizationContext`.
2. Added `CacheInterceptor_FaultedPendingStore_DoesNotAbortNextLookup` to prove a failed pending background store no longer aborts the next cache read.
3. Added `RedirectInterceptor_ClonesPreservedBodyAcrossRedirectHop` to prove 307/308-style preserved-body redirects no longer share the original request's backing array.

## Files Modified

| File | Change |
|------|--------|
| `Runtime/Cache/CacheInterceptor.cs` | Suppressed fault propagation from pending background mutations during foreground lookup sequencing. |
| `Runtime/Cache/CacheStoringHandler.cs` | Swallowed/logged synchronous cache-store queue failures after successful response delivery. |
| `Runtime/Core/IHttpHandler.cs` | Tightened partial-data error-sequence contract docs. |
| `Runtime/Middleware/RedirectHandler.cs` | Removed redundant rethrows after completion-source faults. |
| `Runtime/Middleware/RedirectInterceptor.cs` | Always copies redirect-request bodies; documented ownership rationale. |
| `Runtime/Observability/MonitorHandler.cs` | Cached response-capture limit per response start. |
| `Runtime/Retry/RetryDetectorHandler.cs` | Documented buffered transport/drain assumption for suppressed retryable 5xx callbacks. |
| `Runtime/Testing/MockTransport.cs` | Snapshotted fallback response data before response disposal. |
| `Tests/Runtime/AssertAsync.cs` | Routed `ThrowsAsync(...)` overloads through `Task.Run(...)`. |
| `Tests/Runtime/AssertAsyncTests.cs` | Added synchronization-context deadlock regressions. |
| `Tests/Runtime/Cache/CacheInterceptorTests.cs` | Added faulted pending-store regression and helper storage fixture. |
| `Tests/Runtime/Middleware/RedirectInterceptorTests.cs` | Added redirect-body ownership regression. |

## Assembly Boundary Check

Reviewed the affected assembly definitions:

- `Runtime/Cache/TurboHTTP.Cache.asmdef`
- `Runtime/Observability/TurboHTTP.Observability.asmdef`
- `Runtime/Retry/TurboHTTP.Retry.asmdef`
- `Runtime/Middleware/TurboHTTP.Middleware.asmdef`
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`

No asmdef dependency changes were required. All fixes remain within their existing module boundaries.

## Decisions / Trade-Offs

1. **Background cache mutations remain best-effort:** foreground reads now treat prior mutation failures as sequencing-only signals. The failure is still observable via existing background-work logging, but cache reads continue to the authoritative transport path.
2. **Redirect body safety takes precedence over avoiding one copy:** preserved-body redirect hops now always clone body bytes. That removes a fragile shared-array assumption and keeps request lifetime/ownership rules explicit.
3. **Mock transport favors deterministic lifetime safety over chunk-shape fidelity:** the fallback-response path snapshots response segments before disposal. This is acceptable because `MockTransport` is test infrastructure, not the production hot path.
4. **Retryable 5xx suppression remains valid only for the current buffered transport path:** the code now documents that `RetryDetectorHandler` relies on Phase 22 HTTP/1.1 parsing draining the body before handler callbacks. If a future streaming HTTP/1.1 path lands, this assumption must be revisited.

## Specialist Review Re-Run

Both required rubrics were re-run explicitly against this pass:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Findings Closed

| Finding | Resolution |
|---------|------------|
| P6-B1 | Faulted pending cache mutations no longer abort subsequent cache lookups. |
| P6-B2 | `AssertAsync.ThrowsAsync(...)` no longer blocks the calling test thread directly. |
| P6-W1 | `IHttpHandler` docs now call out partial `OnResponseData(...)` before terminal error delivery. |
| P6-W2 | Redirect completion helpers no longer double-surface the same downstream exception path. |
| P6-W3 | `MonitorHandler` capture limit is cached once per response. |
| P6-W4 | Cache-store queue failures no longer discard already-delivered responses. |
| P6-W5 | Verified/documented that current HTTP/1.1 transport drains before retry handler suppression. |
| P6-W6 | Redirect body bytes are always copied for preserved-body hops. |
| P6-W7 | Mock transport fallback callback delivery no longer depends on source response-body lifetime. |

## Validation

- `git diff --check` passes after the fixes.
- Added targeted regressions for:
  - `AssertAsync.ThrowsAsync(...)` under a non-pumping synchronization context
  - faulted pending cache-store sequencing
  - preserved-body redirect ownership isolation
- Re-ran the infrastructure and network review checklists against the updated cache, redirect, monitor, retry, mock-transport, and test-infrastructure paths.
- Not completed: Unity Test Runner compile/execution, IL2CPP build validation, or device validation. This workspace still has no runnable Unity batchmode/test runner entrypoint or standalone `.sln`/`.csproj`.
