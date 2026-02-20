# Phase 15: Unity Runtime Hardening — 2026-02-20

## What Was Implemented

Phase 15 implementation pass across dispatcher hardening, texture/audio pipeline safety, path/atomic I/O hardening, coroutine lifecycle binding, decoder registry scaffolding, and new Unity runtime reliability/performance tests.

This entry captures the implementation state added in this session.

## Files Created

### Runtime/Unity/
- **MainThreadWorkQueue.cs** — New bounded dispatcher queue with policy-driven backpressure (`Reject`, `Wait`, `DropOldest`), separate user/control queues, bounded waiters, deterministic failure propagation, queue metrics, and low-memory shedding support hooks.
- **PathSafety.cs** — New canonical-path and root-confinement utility. Added atomic write flow (`tmp -> flush -> promote`) plus safe-copy fallback and optional SHA-256/custom integrity verification.
- **LifecycleCancellation.cs** — New lifecycle-bound cancellation service for coroutine wrappers. Handles owner destruction and optional owner-inactive cancellation policy.
- **TextureDecodeScheduler.cs** — New bounded scheduler for async texture decode/finalization with concurrency caps, queue limits, low-memory queue trimming, and warmup hook.
- **UnityTempFileManager.cs** — New centralized temp-file lifecycle manager with sharded subdirectories, startup sweep, bounded I/O concurrency, retry delete queue, and contention metrics.
- **link.xml** — New IL2CPP preserve list for decoder registry/provider types.

### Runtime/Unity/Decoders/
- **IImageDecoder.cs** / **IAudioDecoder.cs** — AOT-safe decoder contracts.
- **DecodedImage.cs** / **DecodedAudio.cs** — Decoded payload DTOs with explicit metadata and data ownership.
- **DecoderRegistry.cs** — Explicit registration/bootstrap, platform policy routing, deterministic resolver/fallback behavior, and warmup APIs.
- **WavPcmDecoder.cs** — Managed WAV PCM/float parser (deterministic parse-stage errors).
- **AiffPcmDecoder.cs** — Managed AIFF PCM parser (COMM/SSND parsing + big-endian sample decode).
- **StbImageSharpDecoder.cs** — PNG/JPEG provider adapter stub (feature-detect fallback when dependency is unavailable).
- **NVorbisDecoder.cs** — OGG provider adapter stub (fallback when dependency unavailable).
- **NLayerMp3Decoder.cs** — MP3 provider adapter stub (fallback when dependency unavailable).

### Tests/Runtime/Unity/
- **MainThreadDispatcherV2Tests.cs** — Queue saturation/backpressure + control-plane isolation checks.
- **TexturePipelineV2Tests.cs** — max-source-bytes/max-pixels guards + threaded decode fallback behavior checks.
- **AudioPipelineV2Tests.cs** — temp-file manager active-file cap + WAV managed decode parsing tests.
- **UnityPathSafetyTests.cs** — traversal/encoded traversal blocking, atomic write corruption protection, checksum checks.
- **CoroutineWrapperLifecycleTests.cs** — owner destruction suppression, exactly-once error path, cancel/fail race behavior.
- **DecoderMatrixTests.cs** — registry selection and optional-provider fallback behavior.
- **UnityReliabilitySuiteTests.cs** — deterministic dispatcher flood completion baseline.
- **UnityStressSuiteTests.cs** — synthetic queue pressure with bounded depth assertions.
- **UnityPerformanceBudgetTests.cs** — queue-depth and temp-file metric guardrails.

## Files Modified

### Runtime/Unity/
- **MainThreadDispatcher.cs**
  - Upgraded lifecycle model (`Uninitialized/Initializing/Ready/Disposing/Reloading`).
  - Captures startup Unity synchronization context + managed main thread id.
  - Added PlayerLoop dispatch integration (with `Update()` fallback).
  - Added queue/backpressure integration through `MainThreadWorkQueue`.
  - Added frame budgets (`MaxItemsPerFrame`, `MaxWorkTimeMs`) using `Stopwatch` timing.
  - Added deterministic rejection/cancellation during shutdown/reload transitions.
  - Added queue + dispatch metrics surface and control-plane dispatch API.
  - Added low-memory queue shedding path.

- **Texture2DHandler.cs**
  - Added Phase 15 policy controls (`MaxSourceBytes`, `MaxPixels`, concurrency/queue limits, threaded decode thresholds).
  - Added decode scheduling through `TextureDecodeScheduler`.
  - Added optional managed-threaded decode route via `DecoderRegistry` with deterministic fallback to Unity `Texture2D.LoadImage`.
  - Added warmup path and per-request metadata diagnostics for queue/decode timing.

- **AudioClipHandler.cs**
  - Replaced inline temp-file behavior with `UnityTempFileManager`.
  - Added bounded decode concurrency and configurable streaming-mode routing for larger assets.
  - Added optional managed decode route via `DecoderRegistry` with deterministic Unity fallback.
  - Added temp-file metrics surface (`GetTempFileMetrics`).

- **UnityExtensions.cs**
  - Migrated path validation to `PathSafety.ResolvePathWithinRoot`.
  - Switched download persistence to atomic write pipeline.
  - Added overloads accepting `UnityAtomicWriteOptions` for optional integrity checks.

- **CoroutineWrapper.cs**
  - Added lifecycle-bound callback control using `LifecycleCancellation`.
  - Added optional `cancelOnOwnerInactive` policy.
  - Enforced atomic exactly-once terminal callback guard in completion/failure races.

## Design/Compatibility Decisions

1. **Fallback-first safety kept intact:** All threaded/managed decode routes are optional and fall back deterministically to existing Unity-native decode paths when unavailable, disabled, or failing.
2. **No reflection-heavy decoder discovery:** Registry uses explicit bootstrap registration for IL2CPP/AOT safety.
3. **Control-plane isolation in dispatcher:** user-queue backpressure policies cannot evict control-plane operations.
4. **Atomic write default behavior:** Unity extension file helpers now promote temp writes safely; optional checksum hooks are additive and non-breaking.
5. **Dependency stubs for external decoders:** Image/OGG/MP3 provider adapters are scaffolded and intentionally return deterministic "provider unavailable" behavior until external packages are wired.

## Validation Notes

- Full Unity test run and cross-platform certification matrix were **not executed in this session**.
- CI workflow artifacts for Phase 15.6 (`TestResults/unity-platform-matrix.json`) are still pending repository CI workflow integration.
- Decoder provider package wiring (StbImageSharp/NVorbis/NLayer concrete integration) is scaffolded but not yet dependency-complete.

## Remaining Follow-Ups

1. Wire concrete external decoder packages and replace provider stubs with full decode implementations.
2. Add/update CI workflow to enforce Phase 15.6 matrix artifact publication and gating.
3. Run full PlayMode/EditMode reliability/stress/perf suites and platform certification matrix before marking Phase 15 complete.

## Review Remediation Update (2026-02-20)

Follow-up pass completed for `2026-02-phase15-review.md` findings:

- Added all previously missing Phase 15 test files (dispatcher, texture pipeline, audio pipeline, coroutine lifecycle, decoder matrix).
- Fixed texture scheduler shared-policy mutation by moving runtime limits to global `TextureDecodeScheduler.Configure(...)`.
- Updated decoder stubs to report `CanDecode=false` when provider assemblies are unavailable.
- Added `DecoderRegistry.Initialize(Action<DecoderRegistryBuilder>)` for deterministic custom registration before bootstrap sealing.
- Fixed off-thread lifecycle driver creation by enqueueing `EnsureDriver()` to `MainThreadDispatcher` when needed.
- Added `LifecycleCancellation` domain-reload static reset hook.
- Fixed temp-file shard computation overflow edge (`int.MinValue` hash case).
- Expanded `link.xml` preserve entries for decoder interfaces/DTOs.
