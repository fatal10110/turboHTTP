# Phase 15: Unity Runtime Hardening and Advanced Asset Pipeline

**Milestone:** M4 (v1.x "correctness + scale")
**Dependencies:** Phase 11 (Unity Integration), Phase 12 (Editor Tools), Phase 13 (Release), Phase 14 prioritization
**Estimated Complexity:** High
**Critical:** Yes - Production stability for high-load Unity projects

## Overview

Phase 11 intentionally delivers a stable baseline for Unity integration:
- deterministic main-thread dispatch,
- synchronous and predictable asset conversion paths,
- compatibility-first coroutine wrappers.

Phase 15 upgrades that baseline for high concurrency, memory pressure, and large asset workloads. This phase is where we implement the more complex but more correct long-term architecture so these improvements are tracked explicitly and not forgotten.

## Goals

1. Eliminate avoidable frame spikes under bursty Unity API workloads.
2. Bound memory growth during texture/audio conversion.
3. Harden temp-file and lifecycle behavior across crash, domain reload, and app pause/resume.
4. Guarantee callback/cancellation correctness in coroutine workflows tied to object lifetime.
5. Add stress/performance gates so regressions are caught in CI, not in production.

## Compatibility Contract (Release Blocking)

1. Every Phase 15 feature must work on every platform officially supported by TurboHTTP for that release.
2. If a threaded/managed decode path is not certified on a platform, that path must auto-disable and fallback behavior must remain fully functional.
3. Platform-specific behavior must be explicit via documented policy and diagnostics; no silent feature drops.
4. Phase 15 cannot be marked complete until the full cross-platform certification matrix is green.

## Task 15.1: MainThreadDispatcher V2 (PlayerLoop + Backpressure)

**Primary files:**
- `Runtime/Unity/MainThreadDispatcher.cs` (upgrade)
- `Runtime/Unity/MainThreadWorkQueue.cs` (new)
- `Tests/Runtime/Unity/MainThreadDispatcherV2Tests.cs` (new)

**Advanced approach:**

1. Bootstrap on the Unity main thread via `[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]`.
2. Capture both main-thread `ManagedThreadId` and the startup `SynchronizationContext`.
3. Keep `DontDestroyOnLoad` singleton behavior as compatibility fallback, but drive execution through a dedicated PlayerLoop stage to avoid scene object timing coupling.
4. Replace unbounded enqueue with bounded queue + configurable backpressure policy (`Reject`, `Wait`, `DropOldest`).
5. Add per-frame budget controls (`maxItemsPerFrame`, `maxWorkTimeMs`) to prevent long frame stalls.
6. Keep `TaskCompletionSource` completion with `RunContinuationsAsynchronously` and deterministic cancellation on shutdown/reload.
7. Expose queue depth and dispatch latency metrics for runtime diagnostics.

**Verification criteria:**

1. Multi-thread stress (`ThreadPool`) under sustained load shows no deadlocks and bounded frame time.
2. Domain reload and play-mode transitions fail pending work deterministically.
3. Queue backpressure behavior matches configured policy.
4. `IsMainThread` remains purely `ManagedThreadId`-based and deterministic.

## Task 15.2: Texture Pipeline V2 (Scheduling + Memory Guards)

**Primary files:**
- `Runtime/Unity/Texture2DHandler.cs` (upgrade)
- `Runtime/Unity/TextureDecodeScheduler.cs` (new)
- `Tests/Runtime/Unity/TexturePipelineV2Tests.cs` (new)

**Advanced approach:**

1. Keep Phase 11 synchronous decode path (`Texture2D.LoadImage`) as stable baseline.
2. Introduce decode scheduling with explicit per-frame budget to prevent N large textures from decoding in one frame.
3. Add policy object for hard limits (`MaxSourceBytes`, `MaxPixels`, `MaxConcurrentDecodes`).
4. Add pre-decode validation using `Content-Length` (when present) plus runtime byte-length checks.
5. Add an opt-in threaded large-asset path:
   - decode compressed image bytes to raw RGBA on worker threads using a pluggable decoder abstraction,
   - keep Unity object creation/upload (`new Texture2D`, `LoadRawTextureData`, `Apply`) on main thread only.
6. Use threshold-based routing (`ThreadedDecodeMinBytes`, `ThreadedDecodeMinPixels`) so small assets stay on the simple baseline path.
7. Support optional experimental async decode path for Unity versions that provide stable async image decode APIs; keep opt-in and feature-gated.
8. Instrument decode duration and allocation estimates in request timeline metadata.
9. Document tradeoffs vs UnityWebRequest: UnityWebRequest benefits from native engine codecs/threads, while TurboHTTP threaded decode is managed/optional and may have different perf/memory profiles per platform.

**Verification criteria:**

1. Large-image bursts are smoothed across frames with configurable throughput.
2. Oversized inputs are rejected before decode.
3. Memory pressure tests confirm no unbounded growth in queued decode workloads.
4. Experimental async path is opt-in and falls back safely when unavailable.
5. Threaded decode path reduces worst-frame stall for large textures versus baseline synchronous path.
6. Threaded decode path preserves deterministic fallback to `LoadImage` when decoder/plugin is unavailable.

## Task 15.3: Audio Pipeline V2 (Temp-File Manager + Concurrency Safety)

**Primary files:**
- `Runtime/Unity/AudioClipHandler.cs` (upgrade)
- `Runtime/Unity/UnityTempFileManager.cs` (new)
- `Tests/Runtime/Unity/AudioPipelineV2Tests.cs` (new)

**Advanced approach:**

1. Centralize temp-file lifecycle in a dedicated manager under `Application.temporaryCachePath/TurboHTTP/`.
2. Use collision-resistant names (GUID + format extension) and metadata tracking for cleanup and diagnostics.
3. Distinguish short/long clips with policy-based load mode (decompress-on-load vs streaming).
4. Add retryable async deletion queue plus startup sweep for orphaned artifacts.
5. Bound concurrent decode operations and temp-file count to avoid I/O thrash under load.
6. Add opt-in threaded audio decode path for large clips:
   - decode compressed formats to PCM on worker threads using pluggable decoders,
   - create/finalize `AudioClip` on main thread, with chunked `SetData` or streaming callback modes for large payloads.
7. Route by size/duration thresholds so short clips keep the simpler baseline path.
8. Emit structured diagnostics on cleanup failures without failing successful request completion.

**Verification criteria:**

1. High-concurrency audio loads produce zero filename collisions.
2. Forced-failure and cancellation paths leave zero orphaned files after cleanup cycle.
3. Startup sweep removes stale files from prior interrupted sessions.
4. Streaming-mode policies reduce peak memory for large clips.
5. Threaded decode path reduces main-thread decode stall for large clips versus baseline path.
6. If threaded decoder is unavailable/unsupported, fallback behavior remains deterministic and documented.

## Task 15.4: Unity Extension I/O Hardening (Canonical Paths + Atomic Writes)

**Primary files:**
- `Runtime/Unity/UnityExtensions.cs` (upgrade)
- `Runtime/Unity/PathSafety.cs` (new)
- `Tests/Runtime/Unity/UnityPathSafetyTests.cs` (new)

**Advanced approach:**

1. Centralize path normalization/validation with canonical root checks.
2. Reject traversal and root-escape attempts after `Path.GetFullPath` canonicalization.
3. Implement atomic write strategy (`.tmp` write + flush + replace/move).
4. Add optional checksum validation for file download helper APIs.
5. Keep all joins via `Path.Combine` and all policy checks cross-platform.

**Verification criteria:**

1. Traversal attempts are blocked consistently across Windows/macOS/Linux.
2. Interrupted writes do not leave corrupted final files.
3. Existing helper APIs remain backward compatible with safe defaults.

## Task 15.5: Coroutine Wrapper Lifecycle Binding

**Primary files:**
- `Runtime/Unity/CoroutineWrapper.cs` (upgrade)
- `Runtime/Unity/LifecycleCancellation.cs` (new)
- `Tests/Runtime/Unity/CoroutineWrapperLifecycleTests.cs` (new)

**Advanced approach:**

1. Add optional owner binding (`MonoBehaviour` / `GameObject`) for automatic cancellation on destroy.
2. Guarantee terminal callback semantics:
   - success callback exactly once on success,
   - error callback exactly once on failure,
   - no callback after owner teardown/cancellation.
3. Ensure callback dispatch on main thread using dispatcher guarantees.
4. Preserve root exception unwrapping and stack fidelity.

**Verification criteria:**

1. Destroying owner before completion cancels work and suppresses success callback.
2. Failure paths still invoke exactly one error callback.
3. Mixed cancellation/failure races remain deterministic.

## Task 15.6: Unity Reliability Test Gate

**Primary files:**
- `Tests/Runtime/Unity/*.cs` (new test suite)
- CI runtime test config updates

**Advanced approach:**

1. Add `[UnityTest]` integration suites for dispatcher, texture, audio, and coroutine lifecycle behavior.
2. Add dedicated stress tests:
   - dispatcher flood from multiple worker threads,
   - large texture burst decode,
   - audio temp-file churn and cleanup.
3. Add performance budgets and regression alerts:
   - max dispatch queue depth,
   - max frame-time impact under synthetic load,
   - max leaked temp files after test cycle.
4. Run mandatory certification matrix for every release candidate:
   - Editor PlayMode (Mono),
   - Standalone players (Windows/macOS/Linux) with IL2CPP where backend is supported,
   - iOS IL2CPP,
   - Android IL2CPP (ARM64),
   - WebGL build + smoke tests when WebGL is in the supported-platform list for that release.
5. Generate a machine-readable compatibility report artifact (`TestResults/unity-platform-matrix.json`) and fail CI on any red cell.

**Verification criteria:**

1. Reliability suite passes consistently in CI.
2. Performance regressions fail fast with actionable metrics.
3. All critical lifecycle and cleanup invariants are covered by automated tests.
4. Certification matrix is green across all officially supported platforms for the release.
5. For platforms where threaded decode is policy-disabled, fallback decode path still passes full asset conversion suites.

## Task 15.7: Decoder Provider Matrix and IL2CPP Constraints

**Primary files:**
- `Runtime/Unity/Decoders/IImageDecoder.cs` (new)
- `Runtime/Unity/Decoders/IAudioDecoder.cs` (new)
- `Runtime/Unity/Decoders/DecodedImage.cs` (new)
- `Runtime/Unity/Decoders/DecodedAudio.cs` (new)
- `Runtime/Unity/Decoders/DecoderRegistry.cs` (new)
- `Runtime/Unity/Decoders/StbImageSharpDecoder.cs` (new)
- `Runtime/Unity/Decoders/WavPcmDecoder.cs` (new)
- `Runtime/Unity/Decoders/AiffPcmDecoder.cs` (new)
- `Runtime/Unity/Decoders/NVorbisDecoder.cs` (new)
- `Runtime/Unity/Decoders/NLayerMp3Decoder.cs` (new)
- `Runtime/Unity/link.xml` (update if stripping requires preserves)

**Recommended default decoder stack (v1.2 target):**

| Asset Type | Formats | Primary Decoder | Fallback | Notes |
|---|---|---|---|---|
| Image | PNG, JPEG | `StbImageSharp` (managed worker-thread decode to RGBA32) | `Texture2D.LoadImage` on main thread | Baseline threaded large-asset path |
| Image | BMP, TGA (optional) | `StbImageSharp` | Reject if disabled by policy | Keep optional to reduce maintenance surface |
| Audio | WAV (PCM) | In-house RIFF parser (`WavPcmDecoder`) | Existing temp-file Unity decode path | No external dependency needed |
| Audio | AIFF (PCM) | In-house FORM parser (`AiffPcmDecoder`) | Existing temp-file Unity decode path | No external dependency needed |
| Audio | OGG/Vorbis | `NVorbis` (managed decode to PCM float) | Existing temp-file Unity decode path | Good IL2CPP compatibility in practice |
| Audio | MP3 | `NLayer` (managed decode to PCM) | Existing temp-file Unity decode path | Validate perf on low-end mobile CPUs |
| Audio | AAC/M4A | No managed default in Phase 15 | Temp-file Unity decode path only | Avoid fragile cross-platform managed AAC stack initially |

**Platform routing policy:**

1. **Editor + Standalone (Mono/IL2CPP):** enable threaded managed decode path by default for assets above configured thresholds.
2. **iOS/Android IL2CPP:** enable threaded decode path with stricter concurrency defaults (`MaxConcurrentDecodes=1..2`) to limit thermal/frame impact.
3. **WebGL:** disable worker-thread decode path by default (single-thread constraints); use baseline path unless future WebGL threading support is explicitly enabled and validated.
4. **Unknown/unsupported platform:** fallback deterministically to baseline path and emit one diagnostic warning per session.

**Platform support matrix policy:**

| Platform | Threaded Decode | Baseline Fallback | Certification Requirement |
|---|---|---|---|
| Editor (Mono) | Required | Required | Must pass on every PR and release candidate |
| Standalone Windows | Required for IL2CPP target | Required | Must pass for release |
| Standalone macOS | Required for IL2CPP target | Required | Must pass for release |
| Standalone Linux | Required for IL2CPP target | Required | Must pass for release |
| iOS (IL2CPP) | Required (policy-tuned concurrency) | Required | Must pass on device/simulator matrix |
| Android (IL2CPP ARM64) | Required (policy-tuned concurrency) | Required | Must pass on ARM64 matrix |
| WebGL (when supported) | Optional/off by default | Required | Build + fallback smoke must pass |

**IL2CPP/AOT constraints (must-haves):**

1. Do not rely on `Reflection.Emit`, runtime code generation, or dynamic method compilation.
2. Register decoders explicitly in `DecoderRegistry` (no assembly scanning).
3. Add `link.xml` preserves only where required; keep preserves minimal and test stripping modes.
4. Keep decode abstractions AOT-safe (no late-bound generic specialization required at runtime).
5. Use bounded worker concurrency and cooperative cancellation; do not use unsupported thread primitives.
6. Keep baseline path fully functional with zero third-party decoder availability.
7. Track third-party licenses and notices for Asset Store packaging compliance.
8. Any third-party decoder dependency must be pinned to a tested version and re-certified on the full matrix before upgrades.
9. Register platform capability probes explicitly (no reflection-based capability checks).

**Decoder selection order:**

1. If threaded decode enabled and format maps to a registered managed decoder, decode on worker thread.
2. If decoder unavailable, unsupported, or policy-disabled, fallback to baseline Unity decode path.
3. If both paths fail, return deterministic `UHttpError` with format, size, and platform details.

**Verification criteria:**

1. Each mapped format has at least one passing integration test on Editor and IL2CPP.
2. Managed decoder path and fallback path produce equivalent pixel/sample dimensions for golden test assets.
3. Stripping/AOT tests confirm decoders are preserved correctly with no runtime missing-method failures.
4. Missing decoder package scenario fails gracefully and falls back without crashes.
5. Each officially supported platform has both primary-path and fallback-path coverage for at least one image and one audio asset.
6. Decoder upgrade tests verify pinned-version compatibility before dependency bumps are merged.

## Definition of Done

1. All Task 15.x verification criteria are automated and green in CI.
2. Advanced policies have conservative defaults and clear opt-in knobs.
3. Documentation includes migration notes from Phase 11 baseline behavior.
4. No breaking API changes without explicit versioning/release notes.
5. Decoder registry, fallback policy, and third-party license notices are documented and validated in release packaging checks.
6. Cross-platform certification report is attached to the release pipeline and confirms support across all declared Unity platforms.

## Notes

- Phase 11 remains the compatibility-first baseline; Phase 15 adds scale/correctness hardening.
- If scope is large, split Task 15.2/15.3 into incremental releases, but keep this document as the canonical backlog.
