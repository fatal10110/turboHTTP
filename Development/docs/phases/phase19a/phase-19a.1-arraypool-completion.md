# Phase 19a.1: `ArrayPool<byte>` Completion Sweep

**Depends on:** Phase 19 (Async Runtime Refactor)
**Estimated Effort:** 1-2 days

---

## Step 0: Promote `ArrayPoolMemoryOwner<T>` to Performance Namespace

**File:** `Runtime/Performance/ArrayPoolMemoryOwner.cs` (new — moved from WebSocket)
**File:** `Runtime/WebSocket/ArrayPoolMemoryOwner.cs` (deleted or redirected)

Required behavior:

1. Move `ArrayPoolMemoryOwner<T>` from `TurboHTTP.WebSocket` namespace to `TurboHTTP.Performance` namespace.
2. Ensure the type implements `IMemoryOwner<T>` with deterministic `Dispose()` that returns the array to `ArrayPool<T>.Shared`.
3. Add a `Length` property returning the originally requested length (not the pooled array length).
4. Integrate with `PooledBuffer<T>` debug wrapper from 19a.0 — in debug builds, `ArrayPoolMemoryOwner<T>` should be constructable via `PooledBuffer<T>` for use-after-return detection.
5. Update all existing WebSocket references to use the new namespace location.

Implementation constraints:

1. Use `[assembly: TypeForwardedTo]` or a simple `using` alias in the WebSocket assembly to avoid breaking internal references during transition, if needed.
2. The type must remain `sealed` for IL2CPP devirtualization.
3. Add XML doc comments explaining ownership semantics: the caller who creates an `ArrayPoolMemoryOwner<T>` owns the buffer and must dispose it.
4. Ensure `.Memory` returns `Memory<T>` sliced to the requested length, not the full pooled array length.

---

## Step 1: Convert Remaining `new byte[]` in HTTP/2 Subsystem

**Files modified:**
- `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs`
- `Runtime/Transport/Http2/Http2FrameCodec.cs`
- `Runtime/Transport/Http2/Http2Settings.cs`
- `Runtime/Transport/Http2/HpackEncoder.cs`
- `Runtime/Transport/Http2/HpackHuffman.cs`

Required behavior:

1. **`Http2Connection.Lifecycle`** — Replace `new byte[]` at GOAWAY payload construction (lines ~258, 274) with `ArrayPool<byte>.Shared.Rent()` + `try/finally` return. Wrap in `ArrayPoolMemoryOwner<byte>` for deterministic disposal.
2. **`Http2FrameCodec`** — Replace `new byte[frame.Length]` (line ~67) with pooled buffer. The frame payload buffer must be returned after the frame is processed.
3. **`Http2Settings`** — Replace settings payload `new byte[]` (line ~113) with pooled buffer. Settings frames are small (6 bytes per setting) but frequent.
4. **`HpackEncoder`** — Replace result array allocation (line ~211) with pooled buffer. The result buffer must be returned by the caller after the encoded headers are written to the stream.
5. **`HpackHuffman`** — Replace `new byte[]` allocations (lines ~335, 466) with pooled buffers. Huffman encode/decode buffers are hot-path in header-heavy workloads.

Implementation constraints:

1. Every `Rent()` must have a corresponding `Return()` in a `finally` block or via `IMemoryOwner<T>.Dispose()`.
2. Use `ArrayPoolMemoryOwner<byte>` where buffer lifetime crosses method boundaries (ownership transfer).
3. Use raw `ArrayPool<byte>.Shared.Rent()/Return()` with try/finally where buffer is used and returned within the same method scope.
4. Pass data as `Memory<byte>` or `ReadOnlyMemory<byte>` — never expose raw pooled arrays to consumers.
5. Clear sensitive data (TLS-related, auth headers) before returning buffers to the pool via `clearArray: true` parameter.

---

## Step 2: Convert Remaining `new byte[]` in WebSocket Subsystem

**Files modified:**
- `Runtime/WebSocket/WebSocketConstants.cs`
- `Runtime/WebSocket/WebSocketHandshakeValidator.cs`
- `Runtime/WebSocket/WebSocketMessage.cs`

Required behavior:

1. **`WebSocketConstants`** — Replace key bytes allocation (line ~63) with pooled buffer. The key is 16 bytes; use `ArrayPool<byte>.Shared.Rent(16)` with immediate return after base64 encoding.
2. **`WebSocketHandshakeValidator`** — Replace header, trailing, and prefetch array allocations (lines ~283, 290, 554) with pooled buffers. These are transient buffers used during handshake validation only.
3. **`WebSocketMessage`** — Replace copy array allocation (line ~91) with pooled buffer. The message payload buffer ownership must be tracked via `IMemoryOwner<byte>` for the `IDisposable` lease pattern already present in `WebSocketMessage`.

Implementation constraints:

1. `WebSocketMessage` already uses `IDisposable` buffer lease — integrate `ArrayPoolMemoryOwner<byte>` as the backing implementation.
2. Handshake buffers are cold-path (once per connection) but still should be pooled for consistency.
3. Key generation buffer is tiny (16 bytes) — pooling avoids allocation but rent/return overhead is comparable to allocation. Pool anyway for consistency and to avoid `new byte[]` in the codebase.

---

## Step 3: Convert `EncodingHelper` and `MultipartFormDataBuilder`

**Files modified:**
- `Runtime/Core/EncodingHelper.cs`
- `Runtime/Content/MultipartFormDataBuilder.cs`

Required behavior:

1. **`EncodingHelper`** — Replace encoding buffer allocation (line ~69) with `ArrayPool<byte>.Shared.Rent()`. The buffer must be returned after encoding is complete.
2. **`MultipartFormDataBuilder.Build()`** — Replace internal `MemoryStream` with a pooled buffer strategy:
   - Use `ArrayPoolMemoryOwner<byte>` to back the output buffer.
   - Size the initial buffer based on estimated content size (sum of part sizes + boundary overhead).
   - If the initial buffer is too small, rent a larger buffer, copy, and return the original.
   - Return an `IMemoryOwner<byte>` from `Build()` instead of `byte[]` — callers are responsible for disposal.
   - This is an **internal API change** — update all internal callers to handle `IMemoryOwner<byte>`.

Implementation constraints:

1. `MultipartFormDataBuilder.Build()` return type change is internal-only — no public API break. The public `MultipartFormDataContent` type that wraps this must manage the `IMemoryOwner<byte>` lifecycle.
2. `EncodingHelper` buffer size must be calculated via `Encoding.GetMaxByteCount()` for rent, but actual written length must be tracked for the returned `Memory<byte>` slice.
3. Do NOT over-optimize `MultipartFormDataBuilder` — it is a cold-path (used once per multipart request). The goal is eliminating `MemoryStream` and `ToArray()` copy, not micro-optimizing.

---

## Verification Criteria

1. All `new byte[]` allocations in the listed files are replaced with pooled equivalents.
2. Every `Rent()` has a corresponding `Return()` (verified by code review and `PooledBuffer<T>` debug mode).
3. `ArrayPoolMemoryOwner<T>` is accessible from all assemblies that need it (Core, Transport, WebSocket).
4. No buffer leaks under normal operation — `ActiveCount` diagnostic returns to zero after a full request/response cycle.
5. `MultipartFormDataBuilder.Build()` returns `IMemoryOwner<byte>` and all callers dispose correctly.
6. All existing tests pass without modification (API changes are internal-only).
7. Memory profiler shows zero `new byte[]` allocations on hot paths during a sustained request benchmark.
8. Sensitive data (HPACK headers, TLS handshake) is cleared before buffer return.
