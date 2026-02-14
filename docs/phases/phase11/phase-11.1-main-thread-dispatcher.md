# Phase 11.1: Main Thread Dispatcher

**Depends on:** Phase 10
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Create `MainThreadDispatcher`

**File:** `Runtime/Unity/MainThreadDispatcher.cs`

Required behavior:

1. Provide a singleton dispatcher component available in runtime scenes.
2. Queue actions from background threads and execute them on Unity main thread.
3. Provide async execution APIs for value and non-value work items.
4. Provide `IsMainThread` helper for fast-path direct execution.
5. Fail fast with clear exception when enqueue is attempted while dispatcher is unavailable (shutdown/domain reload window).

Implementation constraints:

1. Capture main thread identity explicitly during dispatcher initialization.
2. Avoid busy-wait loops (`Thread.Sleep`) for completion signaling.
3. Use `TaskCompletionSource`-based completion for async callers with `TaskCreationOptions.RunContinuationsAsynchronously`.
4. Handle domain reload and object destruction without leaking queued work.
5. Track dispatcher lifecycle (`initializing`, `ready`, `disposing/reloading`) and reject new work once disposal/reload begins.
6. Use explicit domain-reload hooks (for example `AssemblyReloadEvents` / `RuntimeInitializeOnLoadMethod`) to reset static state and cancel pending work with deterministic error propagation.
7. Document unavoidable Editor limitation: work queued during domain reload can be canceled/abandoned and callers must treat it as non-recoverable.

---

## Verification Criteria

1. Work queued from worker threads executes on Unity main thread.
2. Exceptions inside queued work propagate to awaiting callers.
3. Dispatcher remains stable across scene loads.
4. No deadlocks or frame stalls under high enqueue rate.
5. Domain reload or play-mode transition does not silently succeed queued tasks; affected tasks are canceled/failed deterministically and documented.
