# Phase 19a.1: `ArrayPool<byte>` Completion Sweep

**Depends on:** Phase 19 (async runtime refactor)
**Estimated Effort:** 2-3 days

---

## Step 0: Promote `ArrayPoolMemoryOwner<T>` to Shared Performance Namespace

**File:** `Runtime/Performance/ArrayPoolMemoryOwner.cs` (new)
**File:** `Runtime/WebSocket/ArrayPoolMemoryOwner.cs` (deleted after migration)

Required behavior:

1. Move `ArrayPoolMemoryOwner<T>` from `TurboHTTP.WebSocket` to `TurboHTTP.Performance`.
2. Keep deterministic `Dispose()` returning rented arrays to `ArrayPool<T>.Shared`.
3. Keep logical-length semantics (`Memory` sliced to requested length).
4. Update all runtime references (WebSocket + transport code) to the new namespace.

Implementation constraints:

1. Type remains `sealed`.
2. `Rent(0)` behavior remains allocation-free.
3. Keep optional `TryDetach(...)` for ownership transfer paths.

---

## Step 1: Remove Remaining Hot-Path `new byte[]` in HTTP/2

**Files modified:**
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
- `Runtime/Transport/Http2/Http2FrameCodec.cs`
- `Runtime/Transport/Http2/Http2Settings.cs`
- `Runtime/Transport/Http2/HpackEncoder.cs`
- `Runtime/Transport/Http2/HpackHuffman.cs`

Required behavior:

1. Replace remaining per-operation `new byte[]` allocations with pooled rents.
2. Use `try/finally` or owner disposal for every rent.
3. Use owner types when lifetime crosses method boundaries.
4. Clear sensitive buffers on return when needed.

Implementation constraints:

1. No raw pooled-array exposure outside internal ownership boundaries.
2. Preserve existing HTTP/2 behavior exactly.

---

## Step 2: Remove Remaining Hot-Path `new byte[]` in WebSocket

**Files modified:**
- `Runtime/WebSocket/WebSocketConstants.cs`
- `Runtime/WebSocket/WebSocketHandshakeValidator.cs`
- `Runtime/WebSocket/WebSocketMessage.cs`

Required behavior:

1. Replace transient handshake/key allocations with pooled buffers.
2. Keep `WebSocketMessage` lease ownership explicit and deterministic.
3. Keep detached-copy helper for explicit copy semantics, but mark as cold path.

Implementation constraints:

1. Return all handshake buffers in all error paths.
2. Preserve existing protocol validation and error behavior.

---

## Step 3: Remove Transport Encoding Helper Byte Allocations

**File:** `Runtime/Transport/Internal/EncodingHelper.cs` (modified)

Required behavior:

1. Avoid allocating `byte[]` for routine header encoding paths.
2. Provide span/buffer-writer based helpers used by serializers/parsers.
3. Keep Latin-1 fallback behavior for IL2CPP stripping cases.

Implementation constraints:

1. Do not regress correctness for non-ASCII fallback replacement behavior.
2. Keep existing encoding fallback safety.

---

## Step 4: Prepare Multipart Builder for Buffer-Writer Migration

**File:** `Runtime/Files/MultipartFormDataBuilder.cs` (modified)

Required behavior:

1. Remove `MemoryStream`-specific internals so 19a.2 can switch to `IBufferWriter<byte>` cleanly.
2. Keep current public behavior in this step; full buffer-writer output lands in 19a.2.

Implementation constraints:

1. Avoid introducing additional temporary allocations while refactoring internals.
2. Keep generated multipart payload byte-identical.

---

## Verification Criteria

1. Listed HTTP/2 and WebSocket hot-path `new byte[]` allocations are removed.
2. Every pooled rent has deterministic return/dispose coverage.
3. `ArrayPoolMemoryOwner<T>` works across all dependent assemblies.
4. No buffer leaks (`ActiveCount` returns to baseline after request cycles).
5. Existing tests pass with `EnableZeroAllocPipeline = true`.
