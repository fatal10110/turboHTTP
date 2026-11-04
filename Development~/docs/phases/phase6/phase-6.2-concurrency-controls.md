# Phase 6.2: Concurrency Limiter + Middleware

**Depends on:** Phase 6.1
**Assembly:** `TurboHTTP.Performance`
**Files:** 2 new

---

## Step 1: Build `ConcurrencyLimiter`

**File:** `Runtime/Performance/ConcurrencyLimiter.cs`

Required behavior:

1. Maintain per-key (`host`) `SemaphoreSlim` instances.
2. `ExecuteAsync(key, action, ct)` enforces max concurrent executions per key.
3. Always release semaphore in `finally`.
4. Bound dictionary growth using allocation-time LRU eviction (default `maxHosts = 128`, no background timer).
   - metadata shape: `ConcurrentDictionary<string, (SemaphoreSlim semaphore, long lastAccessTicks)>`
   - eviction trigger: when `Count > maxHosts`, evict oldest idle host by `lastAccessTicks`
5. Shutdown/dispose flow is coordinated:
   - set disposing flag (reject new work)
   - wait active operations to reach zero (timeout default `30s`)
   - dispose semaphores after drain/cancel.

---

## Step 2: Add `ConcurrencyMiddleware`

**File:** `Runtime/Performance/ConcurrencyMiddleware.cs`

Required behavior:

1. Extract host from `request.Uri.Host` with null/empty fallback handling (reject or route to default bucket explicitly).
2. Route request execution through `ConcurrencyLimiter.ExecuteAsync(...)`.
3. Record timeline event when waiting for a slot.
4. Respect cancellation tokens end-to-end.

Implementation constraints:

1. No global lock around all hosts.
2. Middleware must be transparent to response semantics.
3. Logging should be minimal and non-blocking (no per-request `Debug.Log` in hot path by default).
4. Guard against `SemaphoreSlim.WaitAsync` cancellation race by tracking permit acquisition state and releasing only if acquired.
   - required pattern: use acquisition-state flag with a `finally` release path
   - recommended implementation uses timeout overload (`WaitAsync(timeout, ct)`) so acquisition result is explicit (`bool acquired`)
   - release occurs only when `acquired == true`
   - include cancellation-race stress test with permit count invariant assertion

---

## Verification Criteria

1. Requests to same host are capped to configured concurrency.
2. Requests to different hosts do not block each other.
3. Cancellation while waiting does not leak permits.
4. Limiter disposal succeeds only after active operations complete/cancel.
5. Host-key cleanup prevents unbounded dictionary growth in long-running sessions.
6. Disposal under active load does not throw `ObjectDisposedException` from semaphore races.
7. Cancellation stress tests confirm no gradual permit loss under repeated cancel/retry churn.
8. Verification includes churn test that repeatedly cancels during waits and asserts no permit-count drift.
