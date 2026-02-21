# Phase 20: Advanced Content Handlers

**Milestone:** M4 (v1.2+)
**Dependencies:** Phase 4 (Pipeline Infrastructure), Phase 5 (Content Helpers), Phase 15 (Decoder Provider Matrix, Pipeline Hardening)
**Estimated Complexity:** Medium (per handler), Medium-High (aggregate)
**Estimated Effort:** 7-10 weeks total (incremental)
**Critical:** No - Incremental feature additions

## Overview

Extend the HTTP pipeline with transparent response decompression and add Unity asset handlers for common content types. Task 20.1 (Decompression) is an `IHttpMiddleware` that benefits all responses transparently. Tasks 20.2–20.4 are Unity-specific asset handlers following the static handler class pattern established by `Texture2DHandler` and `AudioClipHandler`. All handlers are self-contained modules delivered incrementally — each can ship independently in a minor release.

## Design Principles

- Task 20.1 (Decompression) is implemented as an `IHttpMiddleware` registered in the `UHttpClientOptions.Middlewares` pipeline, operating at the transport/pipeline layer before any content handler sees the body.
- Tasks 20.2–20.4 follow the **static handler class pattern** established by `Texture2DHandler` and `AudioClipHandler` (static extension methods on `UHttpResponse` / `UHttpClient`).
- Unity asset handlers must respect Phase 15 memory and concurrency guardrails, including `MainThreadDispatcher` for main-thread-only Unity APIs.
- Unity asset handlers must use `UnityTempFileManager` for all temp-file operations.
- Each handler must have deterministic fallback behavior when required decoders/codecs are unavailable.
- Platform support is documented per handler (including unsupported/partial cases).

## Tasks

### Task 20.1: Decompression Middleware (gzip / brotli)

**Goal:** Transparent, automatic decompression of `Content-Encoding: gzip` and `Content-Encoding: br` response bodies at the pipeline level.

**Deliverables:**
- `DecompressionMiddleware : IHttpMiddleware` — transparently decompresses all HTTP responses
- On outbound requests: injects `Accept-Encoding: gzip, br` header (configurable)
- On inbound responses: detects `Content-Encoding` header, wraps body stream in `GZipStream` or `BrotliStream`, replaces `ReadOnlyMemory<byte>` body with decompressed bytes, removes `Content-Encoding` header after decompression
- Streaming decompression into a pooled buffer (no double-buffering)
- Fallback: pass-through if decoder unavailable (e.g., Brotli on .NET Standard 2.0), with a logged warning
- Auto-registration: included in default middleware pipeline via `UHttpClientOptions`, can be disabled via `EnableDecompression = false`
- `link.xml` entries preserving `System.IO.Compression.GZipStream` and `System.IO.Compression.BrotliStream` for IL2CPP

**Platform Compatibility:**

| Platform | gzip | Brotli | Notes |
|----------|:----:|:------:|-------|
| Editor + Standalone (Mono/IL2CPP) | ✅ | ✅ | Unity 2021.2+ (.NET Standard 2.1) |
| iOS (IL2CPP) | ✅ | ✅ | Requires AOT verification |
| Android (IL2CPP) | ✅ | ✅ | Requires AOT verification |
| WebGL | ✅ | ❌ | .NET Standard 2.0 — no `BrotliStream` |
| Unity < 2021.2 | ✅ | ❌ | .NET Standard 2.0 — no `BrotliStream` |

**Implementation Notes:**
- Brotli availability gated via runtime capability probe (`Type.GetType("System.IO.Compression.BrotliStream")` at startup), not compile-time `#if`
- gzip is universally available via `System.IO.Compression.GZipStream`
- Middleware must handle `Content-Encoding: identity` (no-op) and unknown encodings (pass-through with warning)

**Estimated Effort:** 1 week

---

### Task 20.2: AssetBundle Handler

**Goal:** Download and load Unity AssetBundles from HTTP responses.

**Deliverables:**
- `AssetBundleHandler` static class — downloads to memory or temp file, loads via `AssetBundle.LoadFromMemoryAsync` or `AssetBundle.LoadFromFile`
- Configurable size threshold for memory vs temp-file routing (default: 16 MB; below → memory, above → temp file)
- Maximum download size guard to prevent OOM (configurable, default: 512 MB)
- CRC/hash integrity verification option via `AssetBundle.LoadFromFileAsync(path, crc)` overload
- Temp-file management via `UnityTempFileManager` (Phase 15) — automatic cleanup on disposal, crash recovery
- Main-thread dispatch for `AssetBundle.LoadFrom*` via `MainThreadDispatcher` (Phase 15)
- `response.AsAssetBundleAsync()` and `await client.GetAssetBundleAsync(url)` convenience APIs
- Returns a wrapper that tracks `AssetBundle` ownership and calls `Unload(false)` on disposal

**Caching Strategy:**
- Phase 10 `CacheMiddleware` operates at the raw HTTP response level (caches response bytes). AssetBundle responses are cacheable by default — cache stores the raw bytes, re-decode on cache hit.
- Unity's built-in `Caching` API (`Caching.IsVersionCached`) is **not** integrated — it is designed for `UnityWebRequest` and is not compatible with custom HTTP clients. Document this limitation explicitly.

**Platform Notes:**
- Supported on all platforms except WebGL (AssetBundle loading differs in WebGL — use `UnityWebRequestAssetBundle` instead)
- Main-thread requirement for `AssetBundle.LoadFrom*` — integrates with Phase 15 dispatcher
- `AssetBundle.LoadFromFile` requires the temp file to persist for the lifetime of the loaded bundle — `UnityTempFileManager` release must be deferred until `Unload()` is called

**Estimated Effort:** 2-3 weeks

---

### Task 20.3: Video Content Handler

**Goal:** Download video content and prepare for Unity `VideoPlayer` consumption.

**Deliverables:**

Two distinct modes with separate APIs:

1. **Download mode** (`VideoHandler.GetVideoFileAsync`): Downloads the video to a temp file via TurboHTTP, returns a local path for `VideoPlayer.url`. Supports progress reporting, cancellation, and authentication headers.
   - Uses `UnityTempFileManager` for temp-file lifecycle management
   - Configurable maximum download size guard (default: 1 GB)
   - Cleanup of temp files on disposal

2. **URL passthrough mode** (`VideoHandler.PrepareVideoUrl`): Configures a `VideoPlayer` with a remote URL — `VideoPlayer` handles its own streaming. TurboHTTP's value-add: applies authentication headers (via custom `VideoPlayer` HTTP headers on supported platforms), validates URL reachability, and provides a consistent API surface.

**Platform Notes:**
- `VideoPlayer` codec support varies by platform
- iOS: H.264/H.265; Android: varies by device; WebGL: browser-dependent
- URL passthrough recommended for streaming; download mode for offline playback or authenticated sources
- Handler documents unsupported format/platform combinations

**Estimated Effort:** 1 week

---

### Task 20.4: 3D Model Handlers (glTF)

**Goal:** Download and parse glTF/GLB 3D models from HTTP.

**Deliverables:**
- `GltfHandler` static class — downloads `.gltf` / `.glb` files
- Integration hook for third-party glTF importers (e.g., `GLTFUtility`, `UnityGLTF`)
- No built-in glTF parser — provide the bridge, not the parser
- `await client.GetGltfAsync(url)` returns a `GltfAsset` descriptor (raw bytes + detected format + metadata) that can be fed to the user's chosen importer
- Optional `IGltfImporter` interface for typed integration: `Task<GameObject> ImportAsync(GltfAsset asset, CancellationToken ct)`

**Platform Notes:**
- No built-in Unity glTF support — requires a third-party package
- Handler is optional and documents the required dependency

**Estimated Effort:** 1 week

---

### Task 20.5: Test Suite & Documentation

**Goal:** Integration tests for all handlers plus platform compatibility documentation.

**Deliverables:**
- Per-handler integration tests: success, malformed payload, cancellation, missing decoder
- Decompression middleware tests: gzip round-trip, brotli round-trip, unknown encoding pass-through, disabled middleware, Brotli-unavailable fallback
- Platform compatibility matrix (documented in handler docs)
- Sample code for each handler
- Verify all Unity handlers respect Phase 15 memory/concurrency guardrails
- Verify decompression middleware integrates correctly with `CacheMiddleware` (decompressed body is cached, not compressed body)

**Estimated Effort:** 1 week

---

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 20.1 Decompression Middleware | Highest | 1w | Phase 4, Phase 5 |
| 20.2 AssetBundle | High | 2-3w | Phase 15 |
| 20.3 Video | Medium | 1w | Phase 15 |
| 20.4 glTF | Low | 1w | Phase 5 |
| 20.5 Tests & Docs | High | 1w | All above |

## Delivery Strategy

Handlers ship incrementally. Suggested release mapping:

| Handler | Target Release |
|---------|---------------|
| Decompression Middleware (gzip/brotli) | v1.2.0 |
| AssetBundle | v1.2.0 |
| Video | v1.3.0 |
| glTF | v1.4.0 |

## Verification Plan

1. Decompression middleware correctly injects `Accept-Encoding` and decompresses gzip/brotli responses transparently.
2. Brotli gracefully falls back to pass-through on platforms without `BrotliStream`.
3. Each Unity handler has ≥1 integration test covering success, malformed payload, and cancellation.
4. Unity handlers are compatible with Phase 15 memory/concurrency guardrails (`MainThreadDispatcher`, `UnityTempFileManager`).
5. Platform support is documented per handler with explicit unsupported-platform behavior.
6. Deterministic fallback/error behavior verified when required decoders are unavailable.
7. AssetBundle handler correctly defers temp-file cleanup until `Unload()`.

## Notes

- Decompression middleware (20.1) is the highest-value item and should ship first — it benefits every HTTP response, not just specific content types.
- Task 20.1 is architecturally distinct from 20.2–20.4: it is an `IHttpMiddleware`, while 20.2–20.4 are static handler classes in the `TurboHTTP.Unity` assembly.
- Tasks 20.2–20.4 can proceed in any order after Phase 15 is complete.
- Task 20.1 can proceed independently as it depends only on Phase 4/5 (pipeline infrastructure + content helpers).
