# Phase 6: Performance & Hardening

**Milestone:** M2 (v0.5 "hardening gate")
**Dependencies:** Phase 5 (Content Handlers)
**Estimated Complexity:** High
**Critical:** Yes - Production performance

## Overview

Optimize TurboHTTP for production use with memory pooling, concurrency/backpressure control, lifecycle hardening, and stress validation.

**Detailed breakdown:** `phase6/overview.md`

This document is authoritative for behavior and safety requirements. Any code snippets are illustrative only.

## Goals

1. Reduce avoidable allocations in hot paths.
2. Add safe per-host concurrency controls.
3. Introduce bounded request queueing with deterministic shutdown.
4. Harden client/resource disposal semantics.
5. Add stress and benchmark gates that catch regressions.

## Implementation Rules (Must-Have)

1. **No unbounded pools:** every pool must have explicit capacity/bucket limits.
2. **No cross-request data leakage:** pooled objects/buffers must be reset before reuse.
3. **No unsafe shutdown semantics:** stop paths must complete or cancel pending operations deterministically.
4. **No Unity API use off main thread:** queue workers/background tasks must never call Unity APIs directly.
5. **No disposal races:** do not dispose active semaphores/resources while operations are in flight.
6. **Platform validation is mandatory:** Phase 6 is not complete until IL2CPP build validation is executed.

## Tasks

### Task 6.1: Pooling Primitives

**Files:**
- `Runtime/Performance/ObjectPool.cs`
- `Runtime/Performance/ByteArrayPool.cs`

Required behavior:

1. `ObjectPool<T>` enforces cap atomically (no race between check and insert).
2. `ObjectPool<T>.Count` is explicitly approximate and thread-safe to read.
3. `ByteArrayPool` supports standard buckets and bounded power-of-two buckets.
4. `ByteArrayPool` rejects non-positive sizes.
5. `ByteArrayPool` supports configurable clearing policy:
   - safety mode: clear on rent (prevents tenant-data leak)
   - optional clear on return for sensitive workloads
6. Large-size behavior is explicit (either pooled with cap or intentionally unpooled with docs).

### Task 6.2: Concurrency Controls

**Files:**
- `Runtime/Performance/ConcurrencyLimiter.cs`
- `Runtime/Performance/ConcurrencyMiddleware.cs`

Required behavior:

1. Per-host semaphore control with bounded map growth (idle eviction/cleanup).
2. `ExecuteAsync` always releases permits in `finally`.
3. Disposal is coordinated: stop new work, wait active work to complete/cancel, then dispose semaphores.
4. Middleware records timeline wait events but does not log noisy per-request messages by default.
5. Cancellation while waiting must not leak permits.
6. `WaitAsync` cancellation race is handled explicitly with acquisition-state tracking before release.

### Task 6.3: Request Queue

**File:** `Runtime/Performance/RequestQueue.cs`

Required behavior:

1. Priority ordering is deterministic (`Critical` -> `High` -> `Normal` -> `Low`).
2. Queue size is derived from enum values, not hard-coded numeric assumptions.
3. `Start(...)` is idempotent/guarded; startup failures are observable.
4. `StopAsync(...)` has explicit mode:
   - graceful drain, or
   - cancel-pending (with `TaskCompletionSource` cancellation)
5. Worker loop handles cancellation without surfacing spurious unhandled exceptions.
6. Queue implements `IDisposable` and releases internal synchronization primitives.
7. Worker execution uses `.ConfigureAwait(false)` and does not rely on Unity thread context.

### Task 6.4: Timeline Optimization

**File:** `Runtime/Core/RequestContext.cs` (update)

Required behavior:

1. Pooled timeline events are fully reset (including mutable dictionaries/state).
2. No pooled object state is shared across live requests.
3. Pooling lifecycle is valid only if request context disposal is guaranteed by caller path.
4. `UHttpClient.SendAsync` must ensure `RequestContext` cleanup in `finally`.

### Task 6.5: Disposal Hardening

**File:** `Runtime/Core/UHttpClient.cs` (update)

Required behavior:

1. Full idempotent `IDisposable` implementation.
2. Disposal of transport and disposable middleware exactly once.
3. `ThrowIfDisposed()` applied to all public request entry points:
   - `Get/Post/Put/Patch/Delete`
   - `SendAsync`
   - any overloads/builders that dispatch requests
4. Defensive handling for middleware collection (no concurrent mutation hazards).

### Task 6.6: Stress & Benchmark Gates

**File:** `Tests/Runtime/Performance/StressTests.cs`

Required behavior:

1. High-concurrency mock-transport stress test.
2. Concurrency-limiter enforcement test.
3. Pool allocation regression test.
4. Long-run leak check with explicit methodology (iterations + GC snapshots).
5. Allocation measurement uses explicit tooling (Unity Profiler + GC counters), not only ad-hoc memory snapshots.

## Validation Criteria

### Success Criteria

- [ ] Pool caps are enforced under contention.
- [ ] No pooled data leakage between requests.
- [ ] Concurrency limits are correct and cancellation-safe.
- [ ] Queue stop semantics are deterministic and documented.
- [ ] `UHttpClient` disposal is idempotent and fully guarded.
- [ ] Stress tests pass reliably in CI-capable environment.

### Performance Gates

- **Primary Phase 6 Gate:** `< 10KB` steady-state allocation per request in benchmark path.
- **Stretch Gate (when hot-path rewrites complete):** `< 1KB` per request.
- **Throughput baseline:** >= 1000 req/s with mock transport.
- **Middleware overhead baseline:** < 1ms/request in benchmark path.
- **Platform gate:** run validation on IL2CPP target build before phase sign-off.

## Notes

- Use deterministic fixtures for performance testing whenever possible.
- Prefer measurable regression budgets over brittle one-off absolute numbers.
- Any deferred optimization must include explicit target phase and rationale.
