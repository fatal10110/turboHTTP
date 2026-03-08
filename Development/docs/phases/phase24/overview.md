# Phase 24 Implementation Plan - Overview

Phase 24 is split into 5 sub-phases. Task 20.1 (decompression middleware) is the highest-leverage item because it improves every response path transparently. Tasks 20.2-20.4 are Unity asset handlers that can ship independently after their dependencies are met. Task 20.5 is the final quality/documentation gate.

**Milestone:** M4 (v1.2+)
**Dependencies:** Phase 4 (Pipeline Infrastructure), Phase 5 (Content Helpers), Phase 15 (Decoder Provider Matrix, Pipeline Hardening)
**Estimated Complexity:** Medium (per handler), Medium-High (aggregate)
**Estimated Effort:** 7-10 weeks total (incremental)
**Critical:** No - Incremental feature additions

## Purpose

Extend TurboHTTP content processing in two directions:

1. Add transport-level transparent decompression (`gzip` / `br`) through middleware.
2. Add Unity-focused asset handlers for AssetBundles, video workflows, and glTF payloads.

The middleware and handlers are intentionally modular so each can be released independently.

## Sub-Phase Index

| Sub-Phase | Name | Depends On |
|---|---|---|
| [20.1](Phase-24.1-decompression-middleware.md) | Decompression Middleware (gzip / brotli) | Phase 4, Phase 5 |
| [20.2](Phase-24.2-assetbundle-handler.md) | AssetBundle Handler | Phase 15 |
| [20.3](Phase-24.3-video-content-handler.md) | Video Content Handler | Phase 15 |
| [20.4](Phase-24.4-gltf-model-handlers.md) | 3D Model Handlers (glTF) | Phase 5 |
| [20.5](Phase-24.5-test-suite-documentation.md) | Test Suite & Documentation | 20.1, 20.2, 20.3, 20.4 |

## Dependency Graph

```text
Phase 4 (done - Pipeline Infrastructure)
Phase 5 (done - Content Helpers)
Phase 15 (done - Runtime Hardening + Decoder Matrix)
    │
    ├── 20.1 Decompression Middleware (independent track)
    ├── 20.2 AssetBundle Handler
    ├── 20.3 Video Content Handler
    ├── 20.4 3D Model Handlers (glTF)
    │
    20.1-20.4
        └── 20.5 Test Suite & Documentation
```

## Design Constraints

1. 20.1 must be implemented as `IHttpMiddleware` in the `UHttpClientOptions.Middlewares` pipeline.
2. 20.2-20.4 must follow the static handler pattern used by existing Unity handlers.
3. Unity handlers must respect Phase 15 guardrails (`MainThreadDispatcher`, `UnityTempFileManager`, deterministic fallback behavior).
4. Decoder availability must be runtime-capability based where platform support differs (not only compile-time switches).
5. Platform support and unsupported behavior must be documented per handler.

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 20.1 Decompression Middleware | Highest | 1w | Phase 4, Phase 5 |
| 20.2 AssetBundle | High | 2-3w | Phase 15 |
| 20.3 Video | Medium | 1w | Phase 15 |
| 20.4 glTF | Low | 1w | Phase 5 |
| 20.5 Tests & Docs | High | 1w | All above |

## Delivery Strategy

| Handler | Target Release |
|---------|---------------|
| Decompression Middleware (gzip/brotli) | v1.2.0 |
| AssetBundle | v1.2.0 |
| Video | v1.3.0 |
| glTF | v1.4.0 |

## Verification Plan

1. Decompression middleware injects `Accept-Encoding` and transparently decodes `gzip`/`br` payloads.
2. Brotli unavailable platforms fall back gracefully with pass-through behavior.
3. Each Unity handler has integration tests for success, malformed payload, and cancellation.
4. Unity handlers conform to Phase 15 memory/concurrency constraints.
5. Platform support and unsupported behavior are explicitly documented.
6. AssetBundle temp-file lifecycle is safe: no premature cleanup while bundle is alive.
7. Decompression behavior remains correct when combined with caching middleware behavior.

## Notes

1. 20.1 is the highest value first shipment because it benefits all requests.
2. 20.1 is architecturally independent from Unity handlers and can be delivered in parallel.
3. 20.2-20.4 can proceed in any order once prerequisite runtime hardening is in place.
