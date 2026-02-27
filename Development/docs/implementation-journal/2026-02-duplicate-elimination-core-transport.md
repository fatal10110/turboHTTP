# Duplicate Elimination — Core & Transport (HTTP/2)

**Date:** 2026-02-27  
**Status:** Complete (targeted duplicate set resolved)

---

## Scope

This pass targeted duplicate implementations introduced or exposed during pooling/core migration, specifically in:

1. Core request/body ownership paths.
2. Transport-local pooled buffer wrappers in HTTP/1.1 and HTTP/2.
3. Repeated HTTP/2 lifecycle frame-send routines.
4. Repeated HPACK literal decode/value-task-source wrapper logic.

---

## Changes Implemented

### 1) Replaced transport-local pooled writer clones with Core primitive

Refactored transport code to reuse `TurboHTTP.Core.Internal.PooledArrayBufferWriter` instead of maintaining ad-hoc local pooled buffer implementations.

- `Runtime/Transport/Http1/Http11RequestSerializer.cs`
  - Reworked nested `PooledHeaderWriter` to wrap `PooledArrayBufferWriter`.
  - Kept Latin-1 write behavior via `EncodingHelper`.
  - Removed local resize/rent/return logic.

- `Runtime/Transport/Http2/HpackEncoder.cs`
  - Removed nested `PooledByteBuffer`.
  - Added reusable `_outputBuffer` (`PooledArrayBufferWriter`) on encoder instance.
  - Converted byte/range appends to span-based helper methods.
  - Implemented `IDisposable` to return encoder-owned pooled buffer at connection dispose.

- `Runtime/Transport/Http2/HpackHuffman.cs`
  - Removed nested `PooledByteAccumulator`.
  - Switched decode accumulation to `PooledArrayBufferWriter`.

### 2) Consolidated `UHttpRequest` constructor and body-owner propagation duplication

- `Runtime/Core/UHttpRequest.cs`
  - Added one canonical private constructor for field initialization.
  - Routed public/internal overloads through the canonical initializer.
  - Added `CopyWith(...)` helper to centralize `BodyOwner` propagation for `WithHeaders`, `WithTimeout`, and `WithMetadata`.
  - Preserved semantics of owner disposal on `WithBody`.

### 3) Removed duplicate `ManualResetValueTaskSourceCore` wrapper logic

- `Runtime/Transport/Http2/PoolableValueTaskSource.cs`
  - Added shared base `ValueTaskSourceCoreWrapper<T>`.
  - `ResettableValueTaskSource<T>` and `PoolableValueTaskSource<T>` now reuse common core wrapper methods.
  - Kept pool-return-on-consumption behavior unchanged in `PoolableValueTaskSource<T>.GetResult`.

### 4) Removed repeated HTTP/2 control-frame send boilerplate

- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
  - Added shared `SendFrameWithPooledPayloadAsync(...)` helper.
  - Routed `WINDOW_UPDATE`, `GOAWAY`, `RST_STREAM`, and keepalive `PING` through helper.
  - Preserved cancellation/object-disposed shutdown behavior and pooled payload return semantics.

### 5) Removed repeated HPACK literal decode branches

- `Runtime/Transport/Http2/HpackDecoder.cs`
  - Introduced `DecodeLiteralHeader(...)` and `DecodeLiteralHeaderName(...)`.
  - Reused helper for incremental-indexing, without-indexing, and never-indexed literal paths.
  - Preserved dynamic table add/no-add behavior per representation type.

### 6) Cleared final token-level clone in Core scan

- `Runtime/Core/AdaptiveMiddleware.cs`
  - Small no-op local variable refactor in `InvokeAsync` policy gate to eliminate final clone hit reported by strict `jscpd` threshold.

---

## Files Modified

1. `Runtime/Core/UHttpRequest.cs`
2. `Runtime/Core/AdaptiveMiddleware.cs`
3. `Runtime/Transport/Http1/Http11RequestSerializer.cs`
4. `Runtime/Transport/Http2/HpackEncoder.cs`
5. `Runtime/Transport/Http2/HpackHuffman.cs`
6. `Runtime/Transport/Http2/PoolableValueTaskSource.cs`
7. `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
8. `Runtime/Transport/Http2/HpackDecoder.cs`

---

## Verification

Executed clone detection during and after refactors:

1. `jscpd Runtime/Core/Internal` -> 0 clones.
2. `jscpd Runtime/Transport/Http1 Runtime/Transport/Http2 Runtime/Core` (targeted areas) -> clone groups resolved.
3. `jscpd Runtime/Core --min-lines 5 --min-tokens 30` -> 0 clones.
4. `jscpd Runtime/Transport/Http2 Runtime/Core --min-lines 8 --min-tokens 60` -> 0 clones.

Full `Runtime/` scan still reports many clones, predominantly in vendored `Runtime/Transport/BouncyCastle/Lib/*` and other module-level intentional/legacy similarities; those were out of scope for this pass.

---

## Notes

1. Behavior-preserving refactors were prioritized over API redesign.
2. No runtime/unit test suite execution was performed as part of this documentation pass; this entry records implemented code changes and clone-scan verification only.
