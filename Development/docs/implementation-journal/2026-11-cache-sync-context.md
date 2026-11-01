# 2026-11 cache-sync-context (#245, #246)

Wave run w1101, lane cache-sync-context.

## What

- **#245, background cache work on the caller's SynchronizationContext.**
  - `CacheInterceptor.RunBackgroundRevalidationAsync` and `CacheInterceptor.StoreResponseAsync` detached
    with `await Task.Yield()`. That captures the caller's context: on Unity's main thread the rest of the
    store or revalidation was posted to the Unity context. A foreground lookup of the same base key waits
    on the pending store (`TryLookupAsync` -> `AwaitPendingMutationAsync`), so a caller blocking the main
    thread (`GetAwaiter().GetResult()`) deadlocked, and background cache work stalled whenever the
    player loop was not ticking.
  - New `Runtime/Cache/ThreadPoolHop.cs`: an internal awaitable whose `IsCompleted` is always false and
    whose continuation is queued with `ThreadPool.(Unsafe)QueueUserWorkItem`. It never looks at
    `SynchronizationContext.Current` or `TaskScheduler.Current`. Both Yields are replaced by
    `await new ThreadPoolHop()`.
  - The store hop keeps the property the Yield provided: `QueueMutationAsync` starts the mutation inline
    under `_pendingMutationLock`, and only the queueing of the continuation happens under the lock; the
    body snapshot and entry preparation run on the pool.
  - Ownership: the cancel-before-start checks moved inside the `try`, so the cloned revalidation request
    and the pooled store body are disposed on that path too (they were skipped before).
  - `IL2CPPCompatibility.CheckAsyncAwait` keeps its `Task.Yield`: it runs inside `Task.Run`, where there is
    no context, so it resumes on the pool. A comment says so.
- **#246, Performance fixtures hang together.** Root cause is #245, not a second defect:
  - Unity orders fixtures alphabetically: `Phase19AllocationGateTests.ThroughputScenarios_*` (the last
    `[Phase19][Throughput]` log lines), `RequestQueueTests`, then
    `StreamingAllocationGateTests.CacheRevalidation200Streaming_*`, the #245 test, which blocks the main
    thread and deadlocks.
  - The "#245 test excluded" runs did not exclude it. With `-testFilter` NUnit passes every child of a
    node whose full name matches; `^TurboHTTP\.Tests\.Performance\.(?!StreamingAllocationGateTests\.CacheRevalidation200)`
    matches the fixture node `TurboHTTP.Tests.Performance.StreamingAllocationGateTests`. Verified: that
    filter ran 54 tests including `CacheRevalidation200Streaming_ResponseStart_DoesNotAllocateFullBody`.
  - Root cause recorded on #246.

## Files

- `Runtime/Cache/ThreadPoolHop.cs` (+ `.meta`), new.
- `Runtime/Cache/CacheInterceptor.cs`: the two hops, cancel checks inside `try`.
- `Runtime/Core/IL2CPPCompatibility.cs`: comment only.
- `Tests/Runtime/Cache/CacheInterceptorTests.BackgroundContext.cs` (+ `.meta`), new:
  `CacheInterceptor_Revalidation200_DoesNotWaitOnCallerSynchronizationContext`.

## Decisions

- An awaitable hop over `Task.Run` at the call sites: no closure, the store mutation keeps its static
  lambda, and the hop sits before any work in both methods. `Task.Run(..., token)` would also drop the
  delegate on cancel-before-start and leak the cloned request.
- The test installs a never-pumping, post-counting `SynchronizationContext` on the test thread and calls
  the pipeline directly (not via `Task.Run`), with bounded `Task.Wait(TimeSpan)` waits. It asserts the
  200 revalidation completes, both stores ran off that context, and nothing was posted to it.
- The main-thread `GetAwaiter().GetResult()` calls in `StreamingAllocationGateTests` stay: the cause was
  library code (#246 criterion 3, library branch), and they now act as a second probe of the #245 class.

## Deferred

- #246 criterion 2 (a full host-editor PlayMode run completes): the lane may only run filtered suites;
  the periodic host-editor run after w1101 closes it. #246 stays open (`Part of #246`).
- Pooled store body still leaks when the interceptor is disposed while the store waits behind a previous
  mutation (`RunQueuedMutationAsync` throws before invoking it): amended onto #71 (v1.1).

## Verified

- `'./Tools~/ci/check.sh'`: OK.
- Unity 2021.3.45f2 host editor, PlayMode, filtered:
  - new test on the unfixed library: FAIL ("The 200 revalidation hung ...").
  - `^TurboHTTP\.Tests\.Cache\.|StreamingAllocationGateTests\.CacheRevalidation200`: 48/48 pass.
  - `^TurboHTTP\.Tests\.Performance\.`: 54/54 pass in 2.0 s (hung before).
  - the #246 negative-lookahead filter: 54/54 pass, `CacheRevalidation200Streaming_*` included.

## Not run

- Full EditMode and PlayMode suites (periodic host-editor run), IL2CPP player, devices.
