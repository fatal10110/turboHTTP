# Phase 20: Advanced Content Handlers

**Milestone:** M4 (v1.2+)
**Dependencies:** Phase 5 (Content Handlers), Phase 15 (Decoder Provider Matrix, Pipeline Hardening)
**Estimated Complexity:** Medium (per handler), Medium-High (aggregate)
**Estimated Effort:** 6-8 weeks total (incremental)
**Critical:** No - Incremental feature additions

## Overview

Extend the content handler system with additional Unity asset types and serialization formats. Each handler is a self-contained module that plugs into the existing Phase 5 / Phase 15 pipeline. Handlers are delivered incrementally — each can ship independently in a minor release.

## Design Principles

- Each handler follows the existing `IContentHandler` / decoder-provider pattern from Phase 5 / Phase 15.
- Handlers must respect Phase 15 memory and concurrency guardrails.
- Each handler must have deterministic fallback behavior when required decoders/codecs are unavailable.
- Platform support is documented per handler (including unsupported/partial cases).

## Tasks

### Task 20.1: Compressed Content Handlers (gzip / brotli)

**Goal:** Automatic decompression of `Content-Encoding: gzip` and `Content-Encoding: br` response bodies.

**Deliverables:**
- `GzipContentDecoder` using `System.IO.Compression.GZipStream`
- `BrotliContentDecoder` using `System.IO.Compression.BrotliStream` (where available)
- Auto-registration via `Accept-Encoding` header injection
- Streaming decompression (no full-body buffering)
- Fallback: pass-through if decoder unavailable, with a logged warning

**Platform Notes:**
- Brotli requires .NET Standard 2.1 / .NET 5+ — may not be available on all Unity targets
- gzip is universally available

**Estimated Effort:** 1 week

---

### Task 20.2: AssetBundle Handler

**Goal:** Download and load Unity AssetBundles from HTTP responses.

**Deliverables:**
- `AssetBundleContentHandler` — downloads to memory or temp file, loads via `AssetBundle.LoadFromMemoryAsync` or `AssetBundle.LoadFromFile`
- CRC/hash integrity verification option
- Caching integration with Unity's built-in AssetBundle cache or Phase 10 cache middleware
- `await client.GetAssetBundleAsync(url)` convenience API

**Platform Notes:**
- Supported on all platforms except WebGL (AssetBundle loading differs in WebGL)
- Main-thread requirement for `AssetBundle.LoadFrom*` — integrates with Phase 15 dispatcher

**Estimated Effort:** 1-2 weeks

---

### Task 20.3: Video Content Handler

**Goal:** Download video content and prepare for Unity `VideoPlayer` consumption.

**Deliverables:**
- `VideoContentHandler` — downloads to a temp file for `VideoPlayer.url` playback
- Streaming support via URL passthrough (let `VideoPlayer` handle its own streaming)
- Progress reporting during download
- Cleanup of temp files on disposal

**Platform Notes:**
- `VideoPlayer` codec support varies by platform
- iOS: H.264/H.265; Android: varies by device; WebGL: browser-dependent
- Handler documents unsupported format/platform combinations

**Estimated Effort:** 1 week

---

### Task 20.4: 3D Model Handlers (glTF)

**Goal:** Download and parse glTF/GLB 3D models from HTTP.

**Deliverables:**
- `GltfContentHandler` — downloads `.gltf` / `.glb` files
- Integration hook for third-party glTF importers (e.g., `GLTFUtility`, `UnityGLTF`)
- No built-in glTF parser — provide the bridge, not the parser
- `await client.GetGltfAsync(url)` returns a model descriptor that can be fed to the user's chosen importer

**Platform Notes:**
- No built-in Unity glTF support — requires a third-party package
- Handler is optional and documents the required dependency

**Estimated Effort:** 1 week

---

### Task 20.5: Test Suite & Documentation

**Goal:** Integration tests for all handlers plus platform compatibility documentation.

**Deliverables:**
- Per-handler integration tests: success, malformed payload, cancellation, missing decoder
- Platform compatibility matrix (documented in handler docs)
- Sample code for each handler
- Verify all handlers respect Phase 15 memory/concurrency guardrails

**Estimated Effort:** 1 week

---

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 20.1 Compression | Highest | 1w | Phase 5 |
| 20.2 AssetBundle | High | 1-2w | Phase 15 |
| 20.3 Video | Medium | 1w | Phase 15 |
| 20.4 glTF | Low | 1w | Phase 5 |
| 20.5 Tests & Docs | High | 1w | All above |

## Delivery Strategy

Handlers ship incrementally. Suggested release mapping:

| Handler | Target Release |
|---------|---------------|
| Compression (gzip/brotli) | v1.2.0 |
| AssetBundle | v1.2.0 |
| Video | v1.3.0 |
| glTF | v1.4.0 |

## Verification Plan

1. Each handler has ≥1 integration test covering success, malformed payload, and cancellation.
2. Handlers are compatible with Phase 15 memory/concurrency guardrails.
3. Platform support is documented per handler.
4. Deterministic fallback/error behavior verified when required decoders are unavailable.

## Notes

- Compression handlers (20.1) are the highest-value item and should ship first — they benefit every HTTP response, not just specific content types.
- Each handler is independent — tasks 20.1–20.4 can proceed in any order.
