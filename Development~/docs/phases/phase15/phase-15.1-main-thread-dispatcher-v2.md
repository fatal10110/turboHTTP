# Phase 15.1: MainThreadDispatcher V2 (PlayerLoop + Backpressure)

**Depends on:** Phase 11
**Assembly:** `TurboHTTP.Unity`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Upgrade Dispatcher Lifecycle and PlayerLoop Integration

**File:** `Runtime/Unity/MainThreadDispatcher.cs` (modify)

Required behavior:

1. Bootstrap dispatcher state via `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`.
2. Capture both main-thread `ManagedThreadId` and startup `UnitySynchronizationContext` (or fail initialization with explicit diagnostic if unavailable).
3. Keep singleton compatibility behavior (`DontDestroyOnLoad`) while moving dispatch execution to an explicit PlayerLoop stage.
4. Preserve `IsMainThread` semantics as strict managed-thread-id comparison.
5. Reject enqueue requests deterministically when dispatcher lifecycle is `disposing/reloading`.
6. Cancel or fail pending items during domain reload/play-mode transitions with deterministic error surface.
7. Keep fast-path direct execution when already on main thread and policy allows.
8. Expose lifecycle state and queue metrics for diagnostics.

Implementation constraints:

1. Do not rely on Unity API probes to infer main-thread identity.
2. No blocking waits (`Wait`, `Result`, `Thread.Sleep`) in dispatch path.
3. Use monotonic timing (`Stopwatch`) for latency and budget checks.
4. Keep startup and reload hooks idempotent to avoid duplicate registration.
5. Destroy duplicate runtime instances created by scene content.
6. Preserve backward-compatible public APIs unless explicitly replaced with a migration path.
7. Use `TaskCompletionSource` with `TaskCreationOptions.RunContinuationsAsynchronously`.
8. Do not leak static state across domain reload boundaries.

---

## Step 2: Add Bounded Queue and Backpressure Policy

**Files:**
- `Runtime/Unity/MainThreadWorkQueue.cs` (new)
- `Runtime/Unity/MainThreadDispatcher.cs` (modify)

Required behavior:

1. Replace unbounded queue behavior with bounded capacity.
2. Implement configurable backpressure policy: `Reject`, `Wait`, `DropOldest`.
3. Add per-frame dispatch budget controls:
   - `MaxItemsPerFrame`
   - `MaxWorkTimeMs`
4. Track queue depth, enqueue/dequeue rate, and per-item dispatch latency.
5. Ensure `Wait` policy supports cancellation and timeout.
6. Ensure `DropOldest` policy surfaces deterministic telemetry for dropped items.
7. Separate control-plane and user-work queues so `DropOldest` can only evict user-work items.
8. Subscribe to `Application.lowMemory` and shed non-critical queued work deterministically under pressure.

Implementation constraints:

1. Queue operations must be thread-safe under sustained `ThreadPool` contention.
2. Avoid lock convoy behavior and unbounded waiter growth.
3. Preserve FIFO semantics except where `DropOldest` policy intentionally overrides order for user-work queue only.
4. Backpressure behavior must be deterministic for fixed timing inputs.
5. Budget enforcement must stop work cleanly and continue next frame without starvation.
6. Critical lifecycle/control messages must never be dropped by backpressure policy.

---

## Step 3: Add Dispatcher V2 Stress Coverage

**File:** `Tests/Runtime/Unity/MainThreadDispatcherV2Tests.cs` (new)

Required behavior:

1. Add multi-thread enqueue flood tests with bounded queue assertions.
2. Validate each backpressure policy under saturation.
3. Validate per-frame budget behavior (items/time caps).
4. Validate deterministic cancellation/failure during domain reload simulation.
5. Verify `IsMainThread` correctness on main and worker threads.
6. Verify control-plane messages are never dropped when user queue saturates.
7. Validate low-memory event handling trims queue depth without deadlock.

---

## Verification Criteria

1. Sustained worker-thread flood does not deadlock and queue depth remains policy-bounded.
2. Backpressure policy behavior matches configured mode under saturation.
3. Frame budget constraints cap dispatcher work without starvation.
4. Shutdown/reload transitions fail pending work deterministically.
5. Dispatcher metrics are available and consistent with observed behavior.
