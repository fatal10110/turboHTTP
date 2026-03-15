# Cache / Dispatch Review Fixes

**Date:** 2026-03-10  
**Scope:** Core buffered dispatch completion and cache immediate-consistency follow-up  
**Status:** Code changes and targeted regressions added; Unity compile/test execution still pending from this workspace

## What Was Implemented

This follow-up fixes two correctness issues found in the interceptor/pipeline review pass:

1. Buffered response collection no longer completes on `OnResponseEnd(...)` alone. `ResponseCollectorHandler` now buffers the response and only resolves the public response task after the outer dispatch task completes successfully, so late post-response faults still reach the caller.
2. Cache writes are now serialized per normalized base key. Background stores remain off the response callback path, but reads, invalidation, and revalidation now wait for prior same-key mutations before touching storage, restoring immediate read-after-write behavior without making `OnResponseEnd(...)` block on cache I/O.
3. Cache revalidation and unsafe invalidation now reuse the shared public buffered collection helper from Core instead of maintaining a duplicate collector path inside the cache module.
4. Added focused regressions for:
   - late faults after `OnResponseEnd(...)` in buffered collection
   - next-request lookup waiting for a pending cache store instead of missing

## Files Modified

| File | Change |
|------|--------|
| `Runtime/Core/Pipeline/ResponseCollectorHandler.cs` | Buffered the response separately from task completion; success now commits only after dispatch completion. |
| `Runtime/Core/Pipeline/DispatchBridge.cs` | Success continuation now commits the buffered response instead of treating normal dispatch completion as an unconditional success after `OnResponseEnd(...)`. |
| `Runtime/Cache/CacheInterceptor.cs` | Added per-base-key mutation queue for stores/removals, made lookups wait for pending same-key mutations, routed revalidation/invalidation storage writes through the queue, and switched buffered cache-side dispatch collection to `TransportDispatchHelper`. |
| `Tests/Runtime/Pipeline/InterceptorPipelineTests.cs` | Added regression coverage for late faults after `OnResponseEnd(...)`. |
| `Tests/Runtime/Cache/CacheInterceptorTests.cs` | Added regression coverage proving the next lookup waits for a pending store and then hits cache. |

## Decisions / Trade-Offs

1. **Dispatch task completion is the true completion gate for buffered callers.** `OnResponseEnd(...)` now only stages the response. This prevents successful early completion from hiding later interceptor or transport faults.
2. **Cache consistency is enforced by ordered mutation visibility, not by moving storage back onto the callback thread.** Background stores still detach with `Task.Yield()`, but same-key lookups now await the queued mutation tail before reading storage.
3. **Same-key mutation failures do not permanently block later cache work.** The mutation queue swallows prior task failures while preserving the original failure on the request that triggered it, so a single failed store/remove does not deadlock future cache activity on that key.

## Specialist Review Re-Run

Both required rubrics were re-applied against this change set:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

Checklist outcome:

- Module boundaries remain intact: `TurboHTTP.Cache` still depends only on `TurboHTTP.Core`.
- No Unity-engine dependency was introduced into Core/cache internals.
- Same-key cache mutations are now serialized under a dedicated lock + task tail, reducing race windows for variant index and storage visibility.
- Buffered response ownership stays explicit: staged responses are disposed on failure/cancel paths before task completion.
- Added focused regression coverage for both reported correctness issues.

## Validation

- `git diff --check` passes for the touched files.
- Not completed: Unity Test Runner execution, Unity compile validation, or device/IL2CPP validation. This workspace still has no runnable `.sln`/`.csproj` or Unity batch test entrypoint available to me.

## Deferred / Remaining

1. Run the affected runtime test suite in Unity Test Runner, especially cache, plugin capability, and pipeline tests.
2. Re-run Phase 22 runtime validation on IL2CPP/mobile targets once Unity test execution is available.
