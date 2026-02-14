# Phase 11.5: Coroutine Wrapper API

**Depends on:** Phase 11.1, Phase 11.4
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Add Coroutine-Compatible Wrappers

**File:** `Runtime/Unity/CoroutineWrapper.cs`

Required behavior:

1. Provide coroutine wrappers for request send and typed JSON helpers.
2. Surface success and error callbacks compatible with legacy MonoBehaviour workflows.
3. Keep wrappers thin over existing async APIs (no duplicated networking logic).

Implementation constraints:

1. Unwrap task exceptions to preserve root cause (avoid opaque aggregate errors).
2. Respect cancellation tokens when provided.
3. Prevent callback invocation after cancellation or object teardown where feasible.
4. Wrap coroutine task completion path in `try/catch` and invoke error callback exactly once on failure.
5. Keep wrappers allocation-light, but treat coroutine wrappers as convenience API; high-frequency paths should prefer async/await APIs.
6. For `AggregateException`, unwrap deterministic root exception and preserve stack fidelity (no `throw ex`).

---

## Verification Criteria

1. Coroutine wrappers return successful responses through callbacks.
2. Error paths invoke error callback with root exception details.
3. JSON coroutine wrapper returns typed payloads equivalent to async API results.
4. Cancellation stops coroutine flow without firing success callback.
5. Aggregate task failures surface deterministic primary exception details without losing stack fidelity.
