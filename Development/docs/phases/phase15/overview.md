# Phase 15 Implementation Plan - Overview

Phase 15 is split into 7 sub-phases. Dispatcher hardening ships first, then asset-pipeline and I/O hardening tracks can proceed in parallel. Reliability/performance certification is the final gate.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [15.1](phase-15.1-main-thread-dispatcher-v2.md) | MainThreadDispatcher V2 (PlayerLoop + Backpressure) | 2 new, 1 modified | Phase 11 |
| [15.2](phase-15.2-texture-pipeline-v2.md) | Texture Pipeline V2 (Scheduling + Memory Guards) | 2 new, 1 modified | 15.1 |
| [15.3](phase-15.3-audio-pipeline-v2.md) | Audio Pipeline V2 (Temp-File Manager + Concurrency Safety) | 2 new, 1 modified | 15.1 |
| [15.4](phase-15.4-unity-extension-io-hardening.md) | Unity Extension I/O Hardening | 2 new, 1 modified | Phase 11 |
| [15.5](phase-15.5-coroutine-wrapper-lifecycle-binding.md) | Coroutine Wrapper Lifecycle Binding | 2 new, 1 modified | 15.1, 15.4 |
| [15.6](phase-15.6-unity-reliability-test-gate.md) | Unity Reliability Test Gate | 3 new, 1 modified | 15.1-15.5, 15.7 |
| [15.7](phase-15.7-decoder-provider-matrix.md) | Decoder Provider Matrix and IL2CPP Constraints | 11 new, 1 modified | 15.2, 15.3 |

## Dependency Graph

```text
Phase 11 (done)
    ├── 15.1 MainThreadDispatcher V2
    │    ├── 15.2 Texture Pipeline V2
    │    │    └── 15.7 Decoder Provider Matrix
    │    ├── 15.3 Audio Pipeline V2
    │    │    └── 15.7 Decoder Provider Matrix
    │    └── 15.5 Coroutine Lifecycle Binding
    └── 15.4 Unity Extension I/O Hardening
         └── 15.5 Coroutine Lifecycle Binding

15.1-15.5 + 15.7
    └── 15.6 Unity Reliability Test Gate
```

Sub-phases 15.2, 15.3, and 15.4 can run in parallel once 15.1 baseline lifecycle behavior is merged.

## Existing Foundation (Phases 11 + 12 + 14)

### Existing Types Used in Phase 15

| Type | Key APIs for Phase 15 |
|------|----------------------|
| `MainThreadDispatcher` | existing main-thread dispatch baseline to upgrade |
| `Texture2DHandler` / `AudioClipHandler` | baseline sync decode/conversion behavior |
| `UnityExtensions` | current file download and helper I/O APIs |
| `CoroutineWrapper` | callback-based wrappers over async request APIs |
| `RequestContext` and monitor timeline | runtime diagnostics and per-request timing metadata |
| `UHttpClientOptions` | policy surfaces for decode limits and fallback behavior |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Unity` | Core, Files, JSON | false | runtime hardening and decoder integration |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | stress, lifecycle, and performance gates |

## Release-Blocking Compatibility Contract

1. Every Phase 15 feature must work on every platform officially supported for that release.
2. If a threaded managed decode path is not certified on a platform, it must auto-disable with deterministic fallback.
3. Platform-specific policy must be explicit through diagnostics and documented defaults; no silent feature drops.
4. Phase 15 is incomplete until certification matrix runs green across supported targets.

## Cross-Cutting Design Decisions

1. Unity API interaction remains main-thread-only; worker threads are for managed decode and preprocessing only.
2. Queue depth, decode concurrency, and temp-file growth must be bounded by explicit policy.
3. Cancellation and lifecycle transitions (domain reload, play-mode transitions, object destroy, app pause) must be deterministic.
4. Fallback behavior remains first-class: no decoder/plugin path is allowed to break baseline `LoadImage`/Unity decode behavior.
5. Security and correctness are default-on for I/O helpers (path canonicalization, traversal protection, atomic writes).
6. Reliability and performance gates are required CI checks before enabling new paths by default.
7. Main-thread control-plane operations must be isolated from user workload so backpressure policies never drop critical lifecycle/control messages.
8. Runtime memory pressure handling must subscribe to `Application.lowMemory` (where supported) to trim queues/pools aggressively.
9. Managed decode and scheduler paths should expose optional warmup hooks to reduce first-use JIT/init frame spikes.

## All Files (24 new, 7 modified planned)

| Area | Planned New Files | Planned Modified Files |
|---|---|---|
| 15.1 Dispatcher V2 | 2 | 1 |
| 15.2 Texture Pipeline V2 | 2 | 1 |
| 15.3 Audio Pipeline V2 | 2 | 1 |
| 15.4 I/O Hardening | 2 | 1 |
| 15.5 Coroutine Lifecycle | 2 | 1 |
| 15.6 Reliability Gate | 3 | 1 |
| 15.7 Decoder Matrix | 11 | 1 |

## Post-Implementation

1. Run Unity runtime stress/performance suites and verify deterministic cancellation/lifecycle behavior.
2. Run the full supported-platform certification matrix and publish `TestResults/unity-platform-matrix.json`.
3. Verify fallback decode paths remain green on platforms where threaded decode is disabled by policy.
4. Confirm no temp-file leaks, queue growth leaks, or path-safety regressions under failure injection.
5. Gate Phase 15 completion on green CI plus documented platform policy defaults.
6. Keep CI throughput stable with tiered execution (PR smoke subset, release-candidate full matrix mandatory).
