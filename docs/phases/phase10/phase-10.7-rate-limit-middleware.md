# Phase 10.7: Rate Limit Middleware and Tests

**Depends on:** Phase 10.4, Phase 10.6
**Assembly:** `TurboHTTP.RateLimit`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new

---

## Step 1: Implement `RateLimitMiddleware`

**File:** `Runtime/RateLimit/RateLimitMiddleware.cs`

Required behavior:

1. Select global or per-host bucket based on `RateLimitPolicy`.
2. Attempt token acquisition before forwarding request.
3. When over limit, apply configured behavior:
   - wait until token available, or
   - fail fast with a deterministic rate-limit error.
4. Record timeline events (`RateLimitExceeded`, `RateLimitWaiting`, `RateLimitAcquired`).

Implementation constraints:

1. Host-bucket dictionary must be bounded to avoid long-session growth.
2. Use explicit eviction policy: bounded LRU + idle timeout (for example `maxHosts=1024`, `idleTimeout=10m`).
3. Host-bucket prune/update operations must be thread-safe under one synchronization strategy.
4. Eviction trigger must be explicit: enforce cap synchronously on bucket creation/lookups when count exceeds `maxHosts` (not only by periodic cleanup).
5. Cleanup cost must be throttled (for example, at most once per second) to avoid scan-heavy hot paths.
6. In fail-fast mode, surface deterministic rate-limit failure (`429` semantic) with actionable error details; in wait mode, preserve cancellation responsiveness.
7. Evictions should be observable through low-volume diagnostics/metrics, not per-request warning spam.
8. Avoid per-request warning logs on hot path unless explicitly enabled.
9. Middleware must remain transparent to successful response semantics.
10. Cancellation while waiting must stop request processing cleanly.

---

## Step 2: Add Focused Unit Tests

**Files:**
- `Tests/Runtime/Cache/CacheMiddlewareTests.cs`
- `Tests/Runtime/RateLimit/TokenBucketTests.cs`

Required behavior:

1. Validate cache hit/miss/revalidation and directive handling.
2. Validate token bucket limit/refill/cancellation behavior.
3. Validate middleware behavior for per-host and global policies.
4. Validate host-bucket eviction/pruning behavior under high host-cardinality traffic.

---

## Verification Criteria

1. Rate limiting is enforced deterministically in tests.
2. Cache and rate-limit middleware coexist without deadlocks or starvation.
3. Host override policies are honored by middleware.
4. Over-limit behavior matches configured mode in both synchronous and async stress paths.
5. No unbounded growth in host-bucket map under randomized host workloads.
6. Randomized workload with large unique-host cardinality converges to configured host-bucket bounds.
