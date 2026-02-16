# Phase 11.1: Main Thread Dispatcher

**Depends on:** Phase 10
**Assembly:** `TurboHTTP.Unity`
**Files:** 1 new

---

## Step 1: Create `MainThreadDispatcher`

**File:** `Runtime/Unity/MainThreadDispatcher.cs`

Required behavior:

1. Provide a singleton dispatcher component available in runtime scenes.
2. Auto-create the dispatcher on first access from the main thread and mark it with `DontDestroyOnLoad` so setup is low-friction and scene transitions do not break dispatch.
3. Queue actions from background threads and execute them on Unity main thread.
4. Provide async execution APIs for value and non-value work items.
5. Provide `IsMainThread` helper for fast-path direct execution using a captured main-thread managed thread ID.
6. Fail fast with clear exception when enqueue is attempted while dispatcher is unavailable (shutdown/domain reload window).

Implementation constraints:

1. Capture main thread identity explicitly during dispatcher initialization using `Thread.CurrentThread.ManagedThreadId`.
2. `IsMainThread` must compare `Thread.CurrentThread.ManagedThreadId` against the captured ID; do not infer thread context from Unity API probes.
3. Avoid busy-wait loops (`Thread.Sleep`) for completion signaling.
4. Use `TaskCompletionSource`-based completion for async callers with `TaskCreationOptions.RunContinuationsAsynchronously`.
5. Handle domain reload and object destruction without leaking queued work.
6. Track dispatcher lifecycle (`initializing`, `ready`, `disposing/reloading`) and reject new work once disposal/reload begins.
7. Use explicit domain-reload hooks (for example `AssemblyReloadEvents` / `RuntimeInitializeOnLoadMethod`) to reset static state and cancel pending work with deterministic error propagation.
8. Document unavoidable Editor limitation: work queued during domain reload can be canceled/abandoned and callers must treat it as non-recoverable.
9. Enforce singleton correctness across scene loads (destroy duplicate instances created by scene content or accidental bootstrap duplication).
10. If lazy initialization is requested from a worker thread before bootstrap, fail with actionable error that tells caller to initialize from main thread first.

---

## Verification Criteria

1. Work queued from worker threads executes on Unity main thread.
2. Exceptions inside queued work propagate to awaiting callers.
3. Dispatcher remains stable across scene loads.
4. No deadlocks or frame stalls under high enqueue rate.
5. Domain reload or play-mode transition does not silently succeed queued tasks; affected tasks are canceled/failed deterministically and documented.
6. Dedicated stress test enqueues from multiple `ThreadPool` workers and verifies ordering/completion under sustained pressure.
7. `IsMainThread` returns deterministic results on both main and worker threads without relying on Unity API calls.
