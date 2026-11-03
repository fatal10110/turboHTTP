# Phase 6.4: RequestContext Timeline Optimization

**Depends on:** Phase 6.1
**Assembly:** `TurboHTTP.Core`
**Files:** 1 modified

---

## Step 1: Introduce Timeline Event Pooling

**File:** `Runtime/Core/RequestContext.cs`

Required behavior:

1. Replace hot-path allocations with pooled `TimelineEvent` instances.
2. Preserve event ordering and elapsed timestamps.
3. Keep API shape of `RecordEvent(...)` unchanged.
4. Reset all mutable event state (including metadata dictionaries) before reuse.
5. `RequestContext` must guard against post-dispose use via internal disposed sentinel checks.

---

## Step 2: Return Events on Dispose

Required behavior:

1. When request context is disposed, return timeline events to pool.
2. Clear per-event mutable state before return.
3. Keep context reuse safe across requests.
4. Ensure `RequestContext` disposal is guaranteed from `UHttpClient.SendAsync` (`try/finally` path).
5. If `UHttpClient.SendAsync` does not currently guarantee disposal, add that guarantee in this sub-phase before enabling pooling.
6. Pool return path is idempotent (double-dispose/double-return safe).

Implementation constraints:

1. No shared mutable event state across live requests.
2. Thread safety must match existing `RequestContext` synchronization model.

---

## Verification Criteria

1. Timeline content remains functionally identical pre/post optimization.
2. Per-request allocations from timeline recording are reduced.
3. No pooled object data leakage between requests.
4. Disposal path is covered by tests (success, exception, cancellation flows).
5. Post-dispose context access is validated to fail fast consistently.
6. Multi-threaded post-dispose access (HTTP/2 continuation-style) is validated to fail safely without silent state corruption.
