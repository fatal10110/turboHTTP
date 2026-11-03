# Phase 6.3: Priority Request Queue

**Depends on:** Phase 6.1
**Assembly:** `TurboHTTP.Performance`
**Files:** 1 new

---

## Step 1: Define Queue Model

**File:** `Runtime/Performance/RequestQueue.cs`

Add:

1. `RequestPriority` enum (`Low`, `Normal`, `High`, `Critical`).
2. `QueuedRequest` payload (request, completion source, priority, cancellation token).
3. Internal per-priority queues.

---

## Step 2: Implement Queue Processing

Required behavior:

1. `Start(executor)` spins background workers with startup guards (idempotent and failure-observable).
2. `EnqueueAsync(...)` pushes requests into priority queue and returns task.
3. Workers always dequeue highest available priority first.
4. `StopAsync()` supports explicit shutdown mode (graceful drain or cancel-pending) and waits worker completion.
5. Pending requests are completed deterministically (`SetResult`, `SetException`, or `SetCanceled`) before shutdown completes.
6. Queue capacity is bounded (default `1000` per priority, configurable).
7. Full-queue behavior is explicit (`TryEnqueue` returns false or `EnqueueAsync` fails fast with typed queue-full error).
8. Queued request cancellation tokens are linked to their `TaskCompletionSource` and registrations are disposed on dequeue/terminal completion.
9. `StopAsync()` must dispose cancellation registrations for any still-pending queued items before returning.
10. Cancellation callbacks must use `TrySetCanceled()` (not `SetCanceled()`) to avoid double-completion faults.
11. Registration cleanup handles callback-vs-dispose race safely (no deadlock on registration disposal).

Implementation constraints:

1. Do not lose queued requests on normal flow.
2. Propagate executor exceptions to per-request task.
3. Ensure cancellation shutdown is deterministic.
4. Do not invoke Unity APIs from queue workers (background context).
5. Queue size allocation must not be hard-coded to enum count assumptions.
6. Implement `IDisposable` for queue-owned synchronization resources.
7. Worker loop catches `OperationCanceledException` explicitly and does not leak unobserved task faults.
8. `StopAsync` includes configurable drain timeout (default `30s`) to avoid indefinite shutdown hangs.

---

## Verification Criteria

1. Critical/high requests are served before normal/low requests.
2. Worker shutdown does not deadlock.
3. Exceptions/cancellations are surfaced to callers.
4. Queue size metrics reflect actual pending counts.
5. No orphaned `TaskCompletionSource` remains incomplete after stop/dispose.
6. Queue full behavior is validated and observable to callers.
