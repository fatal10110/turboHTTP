# Phase 15: Unity Runtime Hardening and Advanced Asset Pipeline

**Milestone:** M4 (v1.x "correctness + scale")
**Dependencies:** Phase 11 (Unity Integration — completed), Phase 12 (Editor Tools), Phase 14 (completed — informs prioritization of decode paths and handler scope)
**Estimated Complexity:** High
**Critical:** Yes - Production stability for high-load Unity projects

## Overview

Phase 11 intentionally delivers a stable baseline for Unity integration:
- deterministic main-thread dispatch,
- synchronous and predictable asset conversion paths,
- compatibility-first coroutine wrappers.

Phase 15 upgrades that baseline for high concurrency, memory pressure, and large asset workloads. This phase is where we implement the more complex but more correct long-term architecture so these improvements are tracked explicitly and not forgotten.

Detailed sub-phase breakdown: [Phase 15 Implementation Plan - Overview](phase15/overview.md)

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

## Sub-Phase Summary

### Task 15.1: MainThreadDispatcher V2 (PlayerLoop + Backpressure)

Upgrade `MainThreadDispatcher` with PlayerLoop-driven execution, bounded work queue, configurable backpressure policies (`Reject`/`Wait`/`DropOldest`), and per-frame budget controls.

→ [Full spec](phase15/phase-15.1-main-thread-dispatcher-v2.md)

### Task 15.2: Texture Pipeline V2 (Scheduling + Memory Guards)

Add decode scheduling with per-frame budgets, memory guard policies, and an opt-in threaded large-asset decode path while preserving the synchronous `Texture2D.LoadImage` baseline.

→ [Full spec](phase15/phase-15.2-texture-pipeline-v2.md)

### Task 15.3: Audio Pipeline V2 (Temp-File Manager + Concurrency Safety)

Extract and harden existing temp-file management into a dedicated `UnityTempFileManager`, add streaming-mode routing for large clips, and bound concurrent decode operations.

→ [Full spec](phase15/phase-15.3-audio-pipeline-v2.md)

### Task 15.4: Unity Extension I/O Hardening (Canonical Paths + Atomic Writes)

Extract and harden existing path validation from `UnityExtensions` into a dedicated `PathSafety` utility, add atomic write strategy, and optional checksum integrity checks.

→ [Full spec](phase15/phase-15.4-unity-extension-io-hardening.md)

### Task 15.5: Coroutine Wrapper Lifecycle Binding

Add optional owner binding (`MonoBehaviour`/`GameObject`) for automatic cancellation on destroy, with exactly-once terminal callback guarantees. Depends on 15.1 (dispatcher guarantees for callback dispatch) and 15.4 (deterministic lifecycle cleanup patterns established in I/O hardening).

→ [Full spec](phase15/phase-15.5-coroutine-wrapper-lifecycle-binding.md)

### Task 15.6: Unity Reliability Test Gate

Add `[UnityTest]` reliability and stress suites, performance budget regression guards, and enforce a CI platform certification matrix across Editor, Standalone, iOS, Android, and WebGL targets.

→ [Full spec](phase15/phase-15.6-unity-reliability-test-gate.md)

### Task 15.7: Decoder Provider Matrix and IL2CPP Constraints

Define AOT-safe decoder abstractions, implement managed decoder providers (StbImageSharp, WAV/AIFF parsers, NVorbis, NLayer), and add platform routing policy with deterministic fallback.

→ [Full spec](phase15/phase-15.7-decoder-provider-matrix.md)

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
- Advanced content handlers (AssetBundle, Video, 3D models, Protobuf, compression) are tracked in [Phase 16](phase-16-advanced-capabilities.md), not Phase 15.
