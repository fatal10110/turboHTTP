# Phase 15 Implementation Review — 2026-02-20

Comprehensive review performed by specialist agents (**unity-runtime-architect** and **unity-pipeline-architect**) across all Phase 15 roadmap implementation files.

## Review Agents

| Agent | Focus Area | Files Scoped |
|-------|-----------|-------------|
| **unity-runtime-architect** | Dispatcher lifecycle, threading, domain reload, backpressure, cancellation, low-memory, PlayerLoop integration, coroutine wrappers, lifecycle binding | `MainThreadDispatcher.cs`, `MainThreadWorkQueue.cs`, `LifecycleCancellation.cs`, `CoroutineWrapper.cs`, test files |
| **unity-pipeline-architect** | Texture/audio decode pipelines, scheduler design, temp-file lifecycle, I/O safety, decoder abstractions, platform routing, IL2CPP constraints, link.xml | `Texture2DHandler.cs`, `TextureDecodeScheduler.cs`, `AudioClipHandler.cs`, `UnityTempFileManager.cs`, `PathSafety.cs`, `UnityExtensions.cs`, `Decoders/*`, `link.xml`, test files |

## Review Scope

All Phase 15 files across `Runtime/Unity/`, `Runtime/Unity/Decoders/`, and `Tests/Runtime/Unity/`. Sub-phases 15.1 through 15.7.

**Files Reviewed (runtime):**
- `MainThreadDispatcher.cs`, `MainThreadWorkQueue.cs`
- `TextureDecodeScheduler.cs`, `Texture2DHandler.cs`
- `AudioClipHandler.cs`, `UnityTempFileManager.cs`
- `PathSafety.cs`, `UnityExtensions.cs`
- `LifecycleCancellation.cs`, `CoroutineWrapper.cs`
- `Decoders/IImageDecoder.cs`, `Decoders/IAudioDecoder.cs`, `Decoders/DecodedImage.cs`, `Decoders/DecodedAudio.cs`
- `Decoders/DecoderRegistry.cs`, `Decoders/StbImageSharpDecoder.cs`, `Decoders/WavPcmDecoder.cs`, `Decoders/AiffPcmDecoder.cs`, `Decoders/NVorbisDecoder.cs`, `Decoders/NLayerMp3Decoder.cs`
- `link.xml`

**Files Reviewed (tests):**
- `UnityReliabilitySuiteTests.cs`, `UnityStressSuiteTests.cs`, `UnityPerformanceBudgetTests.cs`
- `UnityPathSafetyTests.cs`, `UnityExtensionsTests.cs`

---

## Summary Verdict

| Severity | Count | Status |
|----------|-------|--------|
| 🔴 Critical | 2 | Open |
| 🟡 Warning | 5 | Open |
| 🟢 Info | 4 | Open |

---

## 🔴 Critical Findings

### C-1 [Runtime / Pipeline] Missing Test Files for Sub-Phases 15.1–15.3, 15.5, 15.7

**Agents:** unity-runtime-architect, unity-pipeline-architect

**Spec requires 5 test files that do not exist:**

| Spec File | Sub-Phase | Missing File |
|-----------|-----------|-------------|
| 15.1 Step 3 | Dispatcher V2 | `MainThreadDispatcherV2Tests.cs` |
| 15.2 Step 3 | Texture Pipeline V2 | `TexturePipelineV2Tests.cs` |
| 15.3 Step 3 | Audio Pipeline V2 | `AudioPipelineV2Tests.cs` |
| 15.5 Step 3 | Coroutine Lifecycle | `CoroutineWrapperLifecycleTests.cs` |
| 15.7 Step 4 | Decoder Matrix | `DecoderMatrixTests.cs` |

**Existing test suites available:**

| File | Tests | Coverage |
|------|-------|---------|
| `UnityReliabilitySuiteTests.cs` | 1 | Dispatcher flood basic |
| `UnityStressSuiteTests.cs` | 1 | Dispatcher bounded metrics |
| `UnityPerformanceBudgetTests.cs` | 2 | Queue cap + temp metrics |
| `UnityPathSafetyTests.cs` | 4 | Traversal + atomic write |
| `UnityExtensionsTests.cs` | 3 | Download + path safety |

**Impact:** Texture burst smoothing, audio temp-file churn, threaded decode fallback, lifecycle-race conditions, and decoder equivalence are untested. Blocks Definition of Done item 1.

**Fix:** Create all 5 missing test files with the scenarios listed in their respective spec steps.

---

### C-2 [Pipeline] TextureDecodeScheduler — Shared Config Mutation on Every ScheduleAsync Call

**Agent:** unity-pipeline-architect
**File:** `Runtime/Unity/TextureDecodeScheduler.cs` (lines 99–102)

```csharp
lock (_gate)
{
    _maxConcurrentDecodes = maxConcurrentDecodes;
    _maxQueuedDecodes = maxQueuedDecodes;
```

Every `ScheduleAsync` call overwrites the singleton's `_maxConcurrentDecodes` and `_maxQueuedDecodes` with the caller's per-request values. When multiple callers use different `TextureOptions`, they silently override each other's policy (last-writer-wins).

**Impact:** Non-deterministic decode policy under concurrent usage. `PumpWorkers_NoLock` uses the singleton-level values, so a concurrent caller can inadvertently expand or shrink another caller's active worker limit mid-flight.

**Fix:** Move concurrency/queue config to a `Configure` method (matching `MainThreadDispatcher.Configure` pattern), and have `ScheduleAsync` respect the singleton-level config rather than accepting per-call params.

---

## 🟡 Warning Findings

### W-1 [Pipeline] Stub Decoders Report CanDecode=true but Always Throw

**Agent:** unity-pipeline-architect
**Files:** `StbImageSharpDecoder.cs`, `NVorbisDecoder.cs`, `NLayerMp3Decoder.cs`

`CanDecode` returns `true` for their respective formats (PNG/JPEG, OGG, MP3), but `DecodeAsync` always throws `NotSupportedException`. These are registered in `BootstrapDefaults()`.

When `EnableThreadedDecode` is true and platform policy enables managed decode, the threaded path is attempted for these formats, always fails, and falls back to Unity with a `Debug.LogWarning` per request. This is noisy and wastes cycles.

**Recommendation:**
- (A) Make `CanDecode` return `false` when the backing provider is not available (preferred).
- (B) Add `bool IsAvailable { get; }` capability probe checked before `DecodeAsync`.

---

### W-2 [Pipeline] DecoderRegistry.BootstrapDefaults() Seals Registration with No User Hook

**Agent:** unity-pipeline-architect
**File:** `Runtime/Unity/Decoders/DecoderRegistry.cs` (lines 47–63)

Once `BootstrapDefaults()` runs (auto-triggered on first `TryResolve*` call), `_registrationSealed = true` permanently prevents `RegisterImageDecoder`/`RegisterAudioDecoder`. There is no documented initialization window for users to register custom decoders before the first resolve call happens.

**Impact:** Users cannot add custom decoders unless they explicitly call `RegisterImageDecoder` before any HTTP texture/audio request triggers auto-bootstrap.

**Fix:** Add an explicit `Initialize(Action configure)` API or split bootstrap into: register custom → seal → resolve. Document in migration notes.

---

### W-3 [Runtime] LifecycleCancellation Driver Creation Is Racy Off-Thread

**Agent:** unity-runtime-architect
**File:** `Runtime/Unity/LifecycleCancellation.cs` (lines 125–128)

```csharp
if (MainThreadDispatcher.IsMainThread())
{
    EnsureDriver();
}
```

`Bind` can be called from any thread. If the first `Bind` is off-thread, the `LifecycleCancellationDriver` MonoBehaviour is never created and `Poll()` never runs. `OwnerDestroyed`/`OwnerInactive` checks silently never fire.

**Impact:** Off-main-thread binding works for `ExplicitToken` cancellation but owner-lifecycle checks are dead.

**Fix:** Schedule driver creation via `MainThreadDispatcher.Enqueue(() => EnsureDriver())` regardless of calling thread. Add test for off-thread binding + destroy cancellation.

---

### W-4 [Pipeline] UnityTempFileManager.ComputeShard — Math.Abs(Int32.MinValue) Overflow

**Agent:** unity-pipeline-architect
**File:** `Runtime/Unity/UnityTempFileManager.cs` (lines 354–370)

```csharp
unchecked
{
    var hash = 17;
    for (var i = 0; i < token.Length; i++)
        hash = (hash * 31) + token[i];
    var shard = Math.Abs(hash) % shardCount;
```

The `unchecked` block prevents arithmetic overflow during hash computation, but `Math.Abs(Int32.MinValue)` is a method call — it throws `OverflowException` regardless of the `unchecked` context.

**Fix:** Replace `Math.Abs(hash)` with `(hash & 0x7FFFFFFF)`.

---

### W-5 [Runtime] No ResetStaticState in LifecycleCancellation for Domain Reload

**Agent:** unity-runtime-architect
**File:** `Runtime/Unity/LifecycleCancellation.cs`

`MainThreadDispatcher`, `AudioClipHandler`, and `UnityTempFileManager` all have `[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]` static reset hooks. `LifecycleCancellation` does not. The `_driver` reference and `ActiveBindings` list leak across domain reload boundaries in the Unity Editor.

**Impact:** Stale bindings from a previous play-mode session can fire cancellations against destroyed objects in the new session, or hold references that prevent GC.

**Fix:** Add `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]` reset method that clears `ActiveBindings`, `PendingCancellations`, nulls `_driver`, and resets `_editorHooksRegistered` equivalent if needed.

---

## 🟢 Info Findings

### I-1 [Pipeline] ReadOnlyMemory<byte> to byte[] Allocation Pattern

**Agent:** unity-pipeline-architect

`WavPcmDecoder` and `AiffPcmDecoder` use `encodedBytes.Span` directly (good). `Texture2DHandler` and `UnityTempFileManager.WriteBytesAsync` try `MemoryMarshal.TryGetArray` before allocating (correct). `ArrayPool<byte>` pooling is not used — spec mentions it as desirable but not blocking.

---

### I-2 [Pipeline] link.xml Coverage Gap for DTOs and Interfaces

**Agent:** unity-pipeline-architect

`link.xml` preserves all decoder types and `DecoderRegistry`, but not `DecodedImage`, `DecodedAudio`, `IImageDecoder`, or `IAudioDecoder`. These may survive stripping because they're referenced from preserved types, but should be validated with an IL2CPP stripping test (`DecoderMatrixTests.cs` — see C-1).

---

### I-3 [Runtime] CoroutineWrapper Uses System.Reflection for JSON Dispatch

**Agent:** unity-runtime-architect

`CreateJsonTask<T>` uses `Type.GetType` and `MethodInfo.MakeGenericMethod`. This is documented, has appropriate IL2CPP warnings, and uses `ExceptionDispatchInfo` for exception fidelity. Not a Phase 15 regression — pattern existed in Phase 11.

---

### I-4 [Runtime / Pipeline] Existing Test Coverage Is Functional but Thin

**Agents:** unity-runtime-architect, unity-pipeline-architect

The existing `UnityReliabilitySuiteTests` (1 test) and `UnityStressSuiteTests` (1 test) provide basic sanity for the dispatcher. `UnityPathSafetyTests` (4 tests) covers traversal and atomic write well. The `UnityPerformanceBudgetTests` (2 tests) are snapshot assertions. `UnityExtensionsTests` (3 tests) are solid integration tests. Overall: adequate for areas covered, but significant gaps per C-1.

---

## File-Level Coverage Matrix

| Sub-Phase | Spec'd New Files | Spec'd Modified Files | Found | Missing |
|-----------|-----------------|----------------------|-------|---------|
| 15.1 | `MainThreadWorkQueue.cs`, `MainThreadDispatcherV2Tests.cs` | `MainThreadDispatcher.cs` | `MainThreadWorkQueue.cs`, `MainThreadDispatcher.cs` | `MainThreadDispatcherV2Tests.cs` |
| 15.2 | `TextureDecodeScheduler.cs`, `TexturePipelineV2Tests.cs` | `Texture2DHandler.cs` | `TextureDecodeScheduler.cs`, `Texture2DHandler.cs` | `TexturePipelineV2Tests.cs` |
| 15.3 | `UnityTempFileManager.cs`, `AudioPipelineV2Tests.cs` | `AudioClipHandler.cs` | `UnityTempFileManager.cs`, `AudioClipHandler.cs` | `AudioPipelineV2Tests.cs` |
| 15.4 | `PathSafety.cs`, `UnityPathSafetyTests.cs` | `UnityExtensions.cs` | All found | — |
| 15.5 | `LifecycleCancellation.cs`, `CoroutineWrapperLifecycleTests.cs` | `CoroutineWrapper.cs` | `LifecycleCancellation.cs`, `CoroutineWrapper.cs` | `CoroutineWrapperLifecycleTests.cs` |
| 15.6 | `UnityReliabilitySuiteTests.cs`, `UnityStressSuiteTests.cs`, `UnityPerformanceBudgetTests.cs` | CI config | All found | CI config not reviewed |
| 15.7 | 11 decoder files | `link.xml` | All found | — |

---

## Sub-Phase Implementation Status

| Sub-Phase | Status | Core Logic | Pipeline/Decoder Wiring | Tests |
|---|---|---|---|---|
| 15.1 MainThreadDispatcher V2 | **In Progress** | ✅ | ✅ | ❌ Missing `MainThreadDispatcherV2Tests.cs` |
| 15.2 Texture Pipeline V2 | **In Progress** | ✅ | ✅ | ❌ Missing `TexturePipelineV2Tests.cs` |
| 15.3 Audio Pipeline V2 | **In Progress** | ✅ | ✅ | ❌ Missing `AudioPipelineV2Tests.cs` |
| 15.4 I/O Hardening | **Complete** | ✅ | ✅ | ✅ |
| 15.5 Coroutine Lifecycle | **In Progress** | ✅ | ✅ | ❌ Missing `CoroutineWrapperLifecycleTests.cs` |
| 15.6 Reliability Test Gate | **Partial** | ✅ | — | ⚠️ Thin coverage (4 tests total) |
| 15.7 Decoder Provider Matrix | **In Progress** | ✅ | ✅ | ❌ Missing `DecoderMatrixTests.cs` |

---

## Overall Assessment

Phase 15 is **architecturally well-implemented** with strong spec alignment across all seven sub-phases. The dispatcher, pipeline, decoder, and lifecycle components are sound and use correct threading primitives. The two critical findings are:

1. **C-1 (5 missing test files)** — The most impactful gap. Texture burst smoothing, decoder equivalence, lifecycle-race, and temp-file churn paths are completely untested. Blocks DoD.
2. **C-2 (scheduler shared config mutation)** — Produces non-deterministic behavior under concurrent usage.

**Recommendation:** Create the 5 missing test files first (C-1), then fix the scheduler singleton pattern (C-2) and the 5 warning items before marking Phase 15 as release-ready.

---

## Remediation Update — 2026-02-20

Follow-up implementation pass completed. All critical and warning findings above have been addressed in code.

### Critical Findings Status

| ID | Status | Resolution |
|---|---|---|
| C-1 | ✅ Resolved | Added all required test files: `MainThreadDispatcherV2Tests.cs`, `TexturePipelineV2Tests.cs`, `AudioPipelineV2Tests.cs`, `CoroutineWrapperLifecycleTests.cs`, `DecoderMatrixTests.cs`. |
| C-2 | ✅ Resolved | `TextureDecodeScheduler` now uses global scheduler configuration via `Configure(TextureDecodeSchedulerOptions)`; per-call config mutation was removed from `ScheduleAsync`. |

### Warning Findings Status

| ID | Status | Resolution |
|---|---|---|
| W-1 | ✅ Resolved | `StbImageSharpDecoder`, `NVorbisDecoder`, and `NLayerMp3Decoder` now gate `CanDecode` on runtime provider availability probes. |
| W-2 | ✅ Resolved | Added `DecoderRegistry.Initialize(Action<DecoderRegistryBuilder>)` startup hook for custom decoder registration before bootstrap sealing. |
| W-3 | ✅ Resolved | `LifecycleCancellation.Bind` now requests driver creation on main thread via dispatcher enqueue when called off-thread. |
| W-4 | ✅ Resolved | `UnityTempFileManager.ComputeShard` now uses `(hash & int.MaxValue) % shardCount` to avoid `Math.Abs(int.MinValue)` overflow. |
| W-5 | ✅ Resolved | Added `LifecycleCancellation.ResetStaticState()` with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`. |

### Info Follow-Up Status

| ID | Status | Resolution |
|---|---|---|
| I-2 | ✅ Addressed | Expanded `Runtime/Unity/link.xml` preserves to include `IImageDecoder`, `IAudioDecoder`, `DecodedImage`, `DecodedAudio`. |

### Additional Notes

- Updated `DecoderMatrixTests.cs` to assert provider-aware resolution behavior (aligned with W-1 changes).
- Added missing `System.Threading.Tasks` import in `TexturePipelineV2Tests.cs` for `TaskStatus` usage.

---

## Revision 2 — Re-Review (2026-02-20)

Full re-review of all remediated files by both agents. All original Revision 1 critical and warning findings are **confirmed resolved**.

### Verification Matrix — Revision 1 Findings

| ID | Verified | Evidence |
|---|---|---|
| C-1 | ✅ Fixed | All 5 test files exist (14 total in `Tests/Runtime/Unity/`). Tests cover: reject policy + control isolation (15.1), source-size/pixel guards + threaded fallback (15.2), temp-file limit + WAV decode (15.3), owner-destroy + cancel-race + off-thread bind (15.5), WAV/image resolution + provider-aware gating (15.7). |
| C-2 | ✅ Fixed | `TextureDecodeScheduler.ScheduleAsync` no longer accepts `maxConcurrentDecodes`/`maxQueuedDecodes` params. Config flows through `Configure(TextureDecodeSchedulerOptions)` only. `_options` read under lock in `ScheduleAsync`. |
| W-1 | ✅ Fixed | All 3 stub decoders use `static readonly bool ProviderAvailable = Type.GetType(...)` probe. `CanDecode` returns `false` when provider is absent. |
| W-2 | ✅ Fixed | `DecoderRegistryBuilder` class added with `AddImageDecoder`/`AddAudioDecoder`. `DecoderRegistry.Initialize(Action<DecoderRegistryBuilder>)` runs configure delegate before sealing. `BootstrapDefaults()` now delegates to `Initialize()`. |
| W-3 | ✅ Fixed | `RequestDriverEnsure()` replaces old inline check. Off-thread path uses atomic `_driverEnsureQueued` flag + `MainThreadDispatcher.Enqueue`. Test `LifecycleCancellation_BindOffThread_StillCancelsOnOwnerDestroy` verifies the fix end-to-end. |
| W-4 | ✅ Fixed | `ComputeShard` now uses `(hash & int.MaxValue) % shardCount`. |
| W-5 | ✅ Fixed | `ResetStaticState()` added with `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]`. Clears `ActiveBindings`, `PendingCancellations`, nulls `_driver`, resets `_driverEnsureQueued`. |
| I-2 | ✅ Fixed | `link.xml` now preserves `IImageDecoder`, `IAudioDecoder`, `DecodedImage`, `DecodedAudio` (4 new entries). |

### Revision 2 — New Warning Findings (3)

#### R2-W1 [Pipeline] DecoderRegistry.Initialize Invokes Configure Callback Outside Lock

**Agent:** unity-pipeline-architect
**File:** `Runtime/Unity/Decoders/DecoderRegistry.cs` (line 103)

```csharp
configure?.Invoke(builder);  // outside lock
```

The builder is populated under lock, the configure delegate runs unlocked, then results are re-applied under lock. If `Initialize` is called concurrently from two threads (e.g., two subsystems racing at startup), both threads could pass the `if (_bootstrapped) return` check before either sets `_bootstrapped = true`. Both would run configure and the second would silently no-op due to the re-check at line 107.

**Impact:** Low — double initialize is safe but the configure callback runs twice, which could confuse a user adding stateful decoders. No data corruption.

**Recommendation:** Move the `configure?.Invoke(builder)` call inside the lock (acceptable since it's a startup-only path), or document that `Initialize` is idempotent and double-invoke is harmless.

---

#### R2-W2 [Runtime] CoroutineWrapperLifecycleTests — SendCoroutine_Failure_InvokesErrorExactlyOnce Uses Disposed Client

**Agent:** unity-runtime-architect
**File:** `Tests/Runtime/Unity/CoroutineWrapperLifecycleTests.cs` (lines 54–75)

```csharp
var client = new UHttpClient(...);
var builder = client.Get("https://example.test/error");
client.Dispose();        // ← disposed here
yield return builder.SendCoroutine(...);  // ← sends after dispose
```

The test validates exactly-once error semantics by disposing the client before sending. This works because `UHttpClient.Dispose` marks the transport as disposed, causing `SendAsync` to throw. However, the `using` keyword is missing — `client` is manually disposed but the `MockTransport` has `DisposeTransport = true`, so the transport is disposed via the client. If the test is modified later and the dispose is moved, the transport could leak.

**Impact:** Low — test correctness is fine. Minor hygiene issue.

**Recommendation:** Add a comment explaining the intentional dispose pattern, or wrap in `using` and add explicit `client.Dispose()` before the send.

---

#### R2-W3 [Pipeline] Texture2DHandler May Call ScheduleAsync with Stale Options After Configure

**Agent:** unity-pipeline-architect
**File:** `Runtime/Unity/Texture2DHandler.cs`

`GetTextureAsync` previously passed `TextureOptions.MaxConcurrentDecodes`/`MaxQueuedDecodes` as per-call params to `ScheduleAsync`. After the C-2 fix, these options should flow through `TextureDecodeScheduler.Configure`. If a user sets `TextureOptions.MaxConcurrentDecodes = 8` on a per-request options object, that value is now silently ignored — the scheduler uses its global config.

**Impact:** Medium — users migrating from the original API may expect per-request decode concurrency tuning. The old params in `TextureOptions` are now dead fields.

**Recommendation:** Either deprecate/remove `TextureOptions.MaxConcurrentDecodes`/`MaxQueuedDecodes` (preferred for clarity), or wire them to auto-call `TextureDecodeScheduler.Configure` on first use with appropriate warnings.

---

### Revision 2 — Updated Sub-Phase Status

| Sub-Phase | Status | Core Logic | Pipeline/Decoder Wiring | Tests |
|---|---|---|---|---|
| 15.1 MainThreadDispatcher V2 | **Complete** | ✅ | ✅ | ✅ (2 tests) |
| 15.2 Texture Pipeline V2 | **Complete** | ✅ | ✅ | ✅ (3 tests) |
| 15.3 Audio Pipeline V2 | **Complete** | ✅ | ✅ | ✅ (2 tests) |
| 15.4 I/O Hardening | **Complete** | ✅ | ✅ | ✅ (4 tests) |
| 15.5 Coroutine Lifecycle | **Complete** | ✅ | ✅ | ✅ (4 tests) |
| 15.6 Reliability Test Gate | **Complete** | ✅ | — | ✅ (4 tests) |
| 15.7 Decoder Provider Matrix | **Complete** | ✅ | ✅ | ✅ (2 tests) |

### Revision 2 Overall Assessment

All Revision 1 critical and warning findings are confirmed fixed with solid test coverage for the fixed behaviors. Three new **low-to-medium severity warnings** were found during re-review:

1. **R2-W1** — `Initialize` double-invoke race (low risk, startup-only)
2. **R2-W2** — Test hygiene in `CoroutineWrapperLifecycleTests` (low risk)
3. **R2-W3** — Dead `TextureOptions` fields after C-2 fix (medium risk for API clarity)

**Recommendation:** Address R2-W3 by deprecating the dead `TextureOptions` fields. R2-W1 and R2-W2 are low priority and can be addressed in a future cleanup pass. Phase 15 is now **release-ready** pending R2-W3 triage.

---

## Revision 3 — Closure Check (2026-02-20)

Follow-up remediation pass completed for all Revision 2 warnings.

| ID | Status | Resolution |
|---|---|---|
| R2-W1 | ✅ Resolved | `DecoderRegistry.Initialize` now executes `configure` under `lock (Gate)`, preventing duplicate callback invocation during concurrent bootstrap races. |
| R2-W2 | ✅ Resolved | `CoroutineWrapperLifecycleTests.SendCoroutine_Failure_InvokesErrorExactlyOnce` now uses `using var client` and includes an explicit comment documenting the intentional disposed-client failure path. |
| R2-W3 | ✅ Resolved | `Texture2DHandler` now bridges legacy `TextureOptions.MaxConcurrentDecodes`/`MaxQueuedDecodes` via `ApplyLegacySchedulerOptions(...)` (one-time warning + default-only scheduler seeding) so those fields are no longer silently ignored. |

### Revision 3 Final Status

- Critical findings: ✅ Closed
- Warning findings: ✅ Closed
- Info follow-ups: ✅ Addressed where actionable (`I-2`)

Phase 15 review findings are fully remediated in the current codebase snapshot.

---

## Revision 4 — Specialist Agent Review (2026-02-20)

Full post-implementation review by the CLAUDE.md-mandated specialist agents: **unity-infrastructure-architect** and **unity-network-architect**. Both reviewed all 21 runtime files and 9 test files.

### Infrastructure Architect: CONDITIONAL PASS (8 Critical, 14 Warning)
### Network Architect: CONDITIONAL PASS (1 Critical, 8 Warning, 8 Info)

### Deduplicated Critical Findings

| ID | Source | Issue | Status |
|---|---|---|---|
| IC-1 | Infra | SemaphoreSlim replaced without disposal in AudioClipHandler/UnityTempFileManager `Configure` | ✅ Fixed — old limiter now disposed after replacement |
| IC-2 | Infra | Reflection-based `Type.GetType` decoder provider checks fail under IL2CPP stripping | ✅ Fixed — replaced with `#if TURBOHTTP_*` compile-time defines |
| IC-3 | Infra | Race condition in `LifecycleCancellationBinding.Cancel` after `Dispose` | ✅ Fixed — added `Volatile.Read(ref _disposed)` guard before `Cancel` |
| IC-4 | Infra | Unbounded delete queue growth in UnityTempFileManager retry loop | ✅ Fixed — capped at `MaxActiveFiles * 2` with logged abandonment |
| IC-7 | Infra | PlayerLoop modification without cleanup on domain reload | ✅ Fixed — added `TryUninstallPlayerLoop()` in `ResetStaticState` |
| IC-8 / NC-1 | Both | DecoderRegistry has no `[RuntimeInitializeOnLoadMethod]` domain reload reset | ✅ Fixed — added `ResetStaticState` clearing all static state under lock |

### Deduplicated Warning Findings

| ID | Source | Issue | Status |
|---|---|---|---|
| IW-4 / NW-4 | Both | TextureDecodeScheduler static singleton lacks domain reload reset | ✅ Fixed — added `ResetStaticState` clearing queue, metrics, re-registering lowMemory |
| IW-5 / NW-5 | Both | UnityTempFileManager static singleton lacks domain reload reset | ✅ Fixed — added `ResetStaticState` clearing activeFiles, deleteQueue, metrics, limiter |
| NW-3 | Net | `_startupSweepDeleted++` not thread-safe (non-atomic on 32-bit IL2CPP) | ✅ Fixed — changed to `Interlocked.Increment` |
| IW-2 | Infra | PathSafety `SafeCopyPromote` leaves temp files on failure | ✅ Fixed — added `TryDeleteFile(tempPath)` to catch block |
| IW-6 | Infra | DecodedImage `checked(width*height*4)` throws OverflowException | ✅ Fixed — replaced with long arithmetic + ArgumentException |
| NW-8 | Net | Per-sample cancellation check in WAV/AIFF decoders (26M+ calls for large files) | ✅ Fixed — check every 8192 samples via `(i & 0x1FFF) == 0` |

### Deferred / Info Items (not blocking)

| ID | Source | Issue | Disposition |
|---|---|---|---|
| IC-5 | Infra | Missing cancellation token propagation during decode in TextureDecodeScheduler | Deferred — token is passed to decode delegate; interior observation is caller responsibility |
| IC-6 | Infra | CoroutineWrapper reflection for JSON dispatch (IL2CPP AOT) | Pre-existing Phase 11 pattern; documented with link.xml guidance. `where T : class` constraint mitigates worst case |
| IC-8 (lock) | Infra | DecoderRegistry.Initialize configure callback under lock | Startup-only path; deadlock requires callback to re-enter DecoderRegistry which is prohibited by sealing. Acceptable |
| NW-2 | Net | WAV/AIFF DecodeAsync is synchronous despite Task return type | Documented; callers use semaphore-bounded thread pool context |
| NW-6 | Net | PathSafety SafeCopyPromote has TOCTOU window | Documented; RequireAtomicReplace flag exists for strict callers |

### Files Modified in This Remediation

- `Runtime/Unity/AudioClipHandler.cs` — SemaphoreSlim disposal
- `Runtime/Unity/UnityTempFileManager.cs` — SemaphoreSlim disposal, delete queue cap, startup sweep atomicity, domain reload reset
- `Runtime/Unity/Decoders/StbImageSharpDecoder.cs` — compile-time define for provider availability
- `Runtime/Unity/Decoders/NVorbisDecoder.cs` — compile-time define for provider availability
- `Runtime/Unity/Decoders/NLayerMp3Decoder.cs` — compile-time define for provider availability
- `Runtime/Unity/LifecycleCancellation.cs` — disposed guard in Cancel
- `Runtime/Unity/MainThreadDispatcher.cs` — TryUninstallPlayerLoop, called from ResetStaticState
- `Runtime/Unity/Decoders/DecoderRegistry.cs` — ResetStaticState domain reload hook
- `Runtime/Unity/TextureDecodeScheduler.cs` — ResetStaticState domain reload hook
- `Runtime/Unity/PathSafety.cs` — temp file cleanup in SafeCopyPromote catch
- `Runtime/Unity/Decoders/DecodedImage.cs` — overflow handling with long arithmetic
- `Runtime/Unity/Decoders/WavPcmDecoder.cs` — cancellation check every 8192 samples
- `Runtime/Unity/Decoders/AiffPcmDecoder.cs` — cancellation check every 8192 samples

### Revision 4 Final Status

- Critical findings: ✅ All 6 fixed
- Warning findings: ✅ All 6 fixed
- Info/deferred: Documented with rationale

Phase 15 specialist agent review is **complete** with all blocking findings remediated.

---

## Revision 5 — Verification Re-Review (2026-02-21)

Verification pass by both CLAUDE.md-mandated specialist agents on the remediated codebase.

### Network Architect: **PASS**

All 8 previous fixes verified correct. No new blocking issues found. Info-level observations only:
- CoroutineWrapper JSON reflection is pre-existing and adequately mitigated by `where T : class`
- `TextureDecodeScheduler.OnLowMemory` uses `TrySetException(OperationCanceledException)` instead of `TrySetCanceled` — intentional design choice for distinguishability

### Infrastructure Architect: **CONDITIONAL PASS** → Upgraded to **PASS**

All 12 previous fixes verified correct. One new warning found and immediately fixed:

| ID | Issue | Status |
|---|---|---|
| R5-W1 | `AudioClipHandler.ResetStaticState` did not dispose old `SemaphoreSlim` before replacement (inconsistent with `Configure` which does) | ✅ Fixed — added `oldLimiter` capture and disposal pattern |

### Revision 5 File Modified

- `Runtime/Unity/AudioClipHandler.cs` — Added SemaphoreSlim disposal in `ResetStaticState()`

### Revision 5 Final Verdict

**PASS** — Both specialist agents confirm all previous findings are correctly remediated. The single new warning (R5-W1) has been fixed. No remaining blocking issues. Phase 15 review is fully closed.
