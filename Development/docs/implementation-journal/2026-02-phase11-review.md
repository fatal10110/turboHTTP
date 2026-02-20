# Phase 11 (Unity Integration) Review — 2026-02-20 (Re-Verified)

Comprehensive review performed by both specialist agents (`unity-infrastructure-architect` and `unity-network-architect`) on the Phase 11 Unity Integration implementation.

This corrected pass re-validates findings against the current `Runtime/Unity/` code and removes claims that were not reproducible from code inspection.

**Re-review (2026-02-20):** All findings independently verified against source code. All confirmed findings, removed findings, and new observations documented below.

## Review Scope

All Phase 11 files under `Runtime/Unity/`:
- `MainThreadDispatcher`
- `Texture2DHandler`
- `AudioClipHandler`
- `UnityExtensions`
- `CoroutineWrapper`

Tests under `Tests/Runtime/Unity/` and assembly definition `TurboHTTP.Unity.asmdef` were also reviewed.

---

## Critical Findings (0)

No verified release-blocking defects were confirmed in the current Phase 11 baseline implementation.

---

## High Findings (2)

### H-1 [Both] `GetJsonCoroutine<T>` Reflection Path Is Not AOT-Safe for Arbitrary `T`

**File:** `Runtime/Unity/CoroutineWrapper.cs:41-47,150-205`

`GetJsonCoroutine<T>` resolves `JsonExtensions.GetJsonAsync<T>` via reflection and calls `MakeGenericMethod(typeof(T))`. On IL2CPP, value-type or otherwise unreferenced generic instantiations can fail at runtime if AOT codegen did not materialize them.

**Fix:** Either constrain/document supported `T` shapes (for example reference types only), or add explicit AOT-preservation strategy and runtime guardrails with a deterministic error message.

**Re-review:** CONFIRMED. `CoroutineWrapper.cs:201` calls `MakeGenericMethod(typeof(T))` with no type constraint. `link.xml` preserves the `JsonExtensions` type but does not force AOT generic instantiation. Value types on IL2CPP remain at risk.

---

### H-2 [Infra] Synchronous Unity Helpers Can Block Worker Threads Under Load

**Files:** `Runtime/Unity/MainThreadDispatcher.cs:301-312`, `Runtime/Unity/Texture2DHandler.cs:43-47,90-95`

`Execute()` / `Execute<T>()`, `AsTexture2D`, and `AsSprite` synchronously wait for main-thread execution. This is safe but can starve worker throughput if heavily used from background threads.

Note: the prior draft’s claim about guaranteed main-thread deadlock before initialization was inaccurate; `ExecuteAsync` has a main-thread fast path and explicit worker-thread initialization guards.

**Fix:** Keep APIs for compatibility, but clearly document blocking semantics and prefer async variants in examples. Consider `[Obsolete]`/analyzer guidance for background-thread use of synchronous helpers.

**Re-review:** CONFIRMED. `Execute()` at line 301-304 uses `.GetAwaiter().GetResult()`. The main-thread fast path at line 184-195 correctly handles on-thread calls. Background-thread calls block on `Update()` drain — real starvation risk under load. No XML doc warning on synchronous methods.

---

## Medium Findings (7)

### M-1 [Network] Missing `ConfigureAwait(false)` in Texture2DHandler Async Methods

**File:** `Runtime/Unity/Texture2DHandler.cs:62-73,109`

`GetTextureAsync` and `GetSpriteAsync` await without `ConfigureAwait(false)`, unlike neighboring Unity handlers (`AudioClipHandler`, `UnityExtensions`).

**Fix:** Add `.ConfigureAwait(false)` to library awaits for consistency and to avoid unnecessary context capture.

**Re-review:** CONFIRMED. `Texture2DHandler.cs:65` (`SendAsync`) and line 109 (`GetTextureAsync`) both await without `ConfigureAwait(false)`. `AudioClipHandler` correctly uses it at lines 69, 76, 101, 108, 138. Inconsistent.

---

### M-2 [Infra] Audio Startup Cleanup Sentinel Is Not Reset on Domain Reload

**File:** `Runtime/Unity/AudioClipHandler.cs:34,294-317`

`_startupCleanupCompleted` is set once and never reset with `RuntimeInitializeLoadType.SubsystemRegistration`. In editor sessions with domain-reload-disabled workflows, cleanup may be skipped after first run.

**Fix:** Add a `SubsystemRegistration` reset hook for `_startupCleanupCompleted`.

---

### M-3 [Infra] Temp File Cleanup Is Warning-Only and Single-Pass

**File:** `Runtime/Unity/AudioClipHandler.cs:294-339`

Cleanup failures are logged but not tracked/escalated, and startup cleanup runs once per static lifetime.

**Fix:** Add failure counters/telemetry and optional retry scheduling (Phase 15 temp-file manager is the long-term path).

---

### M-4 [Infra] Defensive `ToArray()` Copies Add Per-Request Allocations

**Files:** `Runtime/Unity/Texture2DHandler.cs:128-129`, `Runtime/Unity/AudioClipHandler.cs:63-65`

Both handlers copy response bodies before handing data to Unity APIs. This is ownership-safe but allocation-heavy for large assets.

**Fix:** Keep current safety behavior for Phase 11 baseline, but track pooled/conditional copy optimization in Phase 15.

---

### M-5 [Infra] `ConcurrentQueue` Adds Allocation Pressure in Dispatch Hot Paths

**File:** `Runtime/Unity/MainThreadDispatcher.cs:58-59`

`ConcurrentQueue<IDispatchWorkItem>` introduces queue-node allocations under high enqueue rates.

**Fix:** Defer to Phase 15 dispatcher hardening (bounded queue/backpressure) or introduce pooling strategy for hot workloads.

---

### M-6 [Infra] Path Comparison Policy Is Risky on Case-Sensitive macOS Volumes

**File:** `Runtime/Unity/UnityExtensions.cs:16-20,113-118`

`OrdinalIgnoreCase` is used for both Windows and macOS. On case-sensitive APFS/HFS setups, case-insensitive prefix checks may be weaker than intended for root-bound path enforcement.

**Fix:** Use an always-ordinal canonical policy (or `Path.GetRelativePath`-based root containment checks) after `GetFullPath`.

---

## Low Findings (4)

| ID | Issue | File |
|----|-------|------|
| L-1 | `TextureOptions.Format` can be misleading because `Texture2D.LoadImage` may override practical texture format expectations | `Runtime/Unity/Texture2DHandler.cs:17,180-183` |
| L-2 | `IsMainThread()` returns false until main-thread ID is captured during bootstrap | `Runtime/Unity/MainThreadDispatcher.cs:317-324` |
| L-3 | Coroutine cancellation semantics (callback suppression vs request abort) are underdocumented | `Runtime/Unity/CoroutineWrapper.cs:94-107` |
| L-4 | `AsTexture2D` does not call `EnsureSuccessStatusCode()` (callers using raw response APIs can see decode errors on non-2xx) | `Runtime/Unity/Texture2DHandler.cs:43-47` |

---

## Findings Removed From Prior Draft

1. **MainThreadDispatcher instance-creation race (removed):** not reproducible with current lock/state flow in `EnsureInstanceReady`.
2. **Guaranteed pre-init deadlock in `Execute()` (removed):** contradicted by current main-thread fast path + initialization guards.
3. **Shutdown enqueue race (removed):** enqueue and shutdown paths both use `LifecycleLock`, preventing the claimed window.
4. **`WriteAsync(ReadOnlyMemory<byte>)` IL2CPP failure as critical blocker (removed):** risk was speculative and not demonstrated by current code evidence.

---

## Positive Observations

### Module Dependency Discipline (Excellent)

`TurboHTTP.Unity.asmdef` references only `TurboHTTP.Core`; JSON support remains optional via reflection.

### Lifecycle and Threading Rigor (Good)

- Explicit dispatcher state machine (`Uninitialized → Initializing → Ready → Disposing`)
- Domain-reload/play-mode shutdown hooks
- Pending-work deterministic cancellation on shutdown
- `TaskCreationOptions.RunContinuationsAsynchronously` usage in async bridges

### Error and Resource Handling (Good)

- Texture decode cleans up allocated Unity objects on failure
- Coroutine wrapper unwraps root task exceptions
- Audio decode uses `try/finally` temp-file cleanup
- Cancellation token registrations are disposed on completion paths

---

## Platform Compatibility Matrix

| Component | Editor | Standalone | iOS (IL2CPP) | Android (IL2CPP) | WebGL |
|-----------|--------|------------|--------------|------------------|-------|
| MainThreadDispatcher | OK (H-2 usage caveat) | OK (H-2) | OK (H-2) | OK (H-2) | N/A (deferred) |
| Texture2DHandler | OK | OK | OK (H-2, M-1, M-4) | OK (H-2, M-1, M-4) | N/A |
| AudioClipHandler | OK | OK | OK (M-2, M-3, M-4) | OK (M-2, M-3, M-4) | N/A |
| UnityExtensions | OK | OK | OK (M-6) | OK (M-6) | N/A |
| CoroutineWrapper | OK | OK | **H-1** | **H-1** | N/A |

---

## Prioritized Fix List

| # | ID | Severity | Description |
|---|----|----------|-------------|
| 1 | H-1 | High | Harden/document IL2CPP+AOT behavior for `GetJsonCoroutine<T>` generic reflection path |
| 2 | H-2 | High | Document/deprecate synchronous helper usage from worker threads; steer to async APIs |
| 3 | M-1 | Medium | Add `ConfigureAwait(false)` in `Texture2DHandler` async methods |
| 4 | M-2 | Medium | Reset `_startupCleanupCompleted` on subsystem registration |
| 5 | M-6 | Medium | Strengthen root path containment checks for case-sensitive filesystems |
| 6 | M-3 | Medium | Add temp-file cleanup failure counters and diagnostics |
| 7 | M-4 | Medium | Track allocation-reduction strategy for response body copying (Phase 15) |
| 8 | M-5 | Medium | Address queue allocation pressure in dispatcher hardening work (Phase 15) |

---

## Overall Assessment

Phase 11 is a solid compatibility-first baseline and appears production-usable for normal Unity workloads. No confirmed critical blockers remain in this corrected review.

The highest-priority remaining work is:
1. IL2CPP/AOT hardening for coroutine JSON generic reflection (`H-1`).
2. Clear guidance around synchronous main-thread helper usage from worker threads (`H-2`).
3. Medium-level cleanup/documentation hardening items aligned with planned Phase 15 work.

**Recommendation:** mark Phase 11 functionally complete with documented limitations, then close `H-1`/`H-2` before broad IL2CPP production rollout.

**Files requiring follow-up changes:**
- `Runtime/Unity/CoroutineWrapper.cs` (`H-1`, `L-3`)
- `Runtime/Unity/MainThreadDispatcher.cs` (`H-2`, `M-5`)
- `Runtime/Unity/Texture2DHandler.cs` (`H-2`, `M-1`, `M-4`, `L-1`, `L-4`)
- `Runtime/Unity/AudioClipHandler.cs` (`M-2`, `M-3`, `M-4`)
- `Runtime/Unity/UnityExtensions.cs` (`M-6`)
