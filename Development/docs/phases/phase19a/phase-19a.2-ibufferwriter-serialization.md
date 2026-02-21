# Phase 19a.2: `IBufferWriter<byte>` Serialization Paths

**Depends on:** 19a.1 (`ArrayPool<byte>` Completion Sweep)
**Estimated Effort:** 1 week

---

## Step 0: Extend `IJsonSerializer` with Buffer-Based Methods

**File:** `Runtime/Core/Serialization/IJsonSerializer.cs` (modified)

Required behavior:

1. Add buffer-based serialization methods to the existing `IJsonSerializer` interface:
    ```csharp
    public interface IJsonSerializer
    {
        // Existing methods (unchanged)
        string Serialize<T>(T value);
        T Deserialize<T>(string json);
        string Serialize(object value, Type type);
        object Deserialize(string json, Type type);

        // New buffer-based methods
        void Serialize<T>(T value, IBufferWriter<byte> output);
        T Deserialize<T>(ReadOnlySequence<byte> input);
    }
    ```
2. The new methods are **additive** — existing implementations continue to work via the string-based methods.
3. Implementations that do not support buffer-based serialization may provide a default bridge implementation (serialize to string → encode to buffer).

Implementation constraints:

1. Adding methods to an existing interface is a **breaking change** for external implementors. Provide default interface method implementations (C# 8.0 DIM) that bridge via the string methods:
    ```csharp
    void Serialize<T>(T value, IBufferWriter<byte> output)
    {
        var json = Serialize(value);
        var bytes = Encoding.UTF8.GetBytes(json);
        output.Write(bytes);
    }
    ```
2. If C# 8.0 DIM is unavailable (Unity's .NET Standard 2.0 target), use a separate `IBufferJsonSerializer` interface that extends `IJsonSerializer`, and detect at runtime via `is IBufferJsonSerializer`.
3. Document that the bridge implementation allocates (string + byte[]) — purpose-built implementations should override for zero-allocation behavior.

---

## Step 1: Update `LiteJson` Serializer with Buffer Support

**File:** `Runtime/Core/Serialization/LiteJsonSerializer.cs` (modified)

Required behavior:

1. Implement the new buffer-based `Serialize<T>` method on `LiteJsonSerializer`.
2. The `LiteJson` implementation bridges via the string-based serializer: `Serialize<T>() → Encoding.UTF8.GetBytes() → output.Write()`.
3. This is intentionally NOT zero-allocation — `LiteJson` is a lightweight built-in serializer. Purpose-built implementations (e.g., `System.Text.Json` adapter) should override for direct buffer writes.
4. Implement the `Deserialize<T>(ReadOnlySequence<byte> input)` method by flattening the sequence to a string and delegating to the existing string-based deserializer.

Implementation constraints:

1. For `Serialize<T>`, use `Encoding.UTF8.GetByteCount(json)` first to request the exact buffer size from `IBufferWriter<byte>.GetSpan()`, then `Encoding.UTF8.GetBytes(json, span)`.
2. For `Deserialize<T>`, if the sequence is single-segment, use `Encoding.UTF8.GetString(segment.Span)` without copying. If multi-segment, flatten to a rented buffer first.
3. Mark the bridge implementations with `[MethodImpl(MethodImplOptions.AggressiveInlining)]` where appropriate.

---

## Step 2: Create `PooledArrayBufferWriter<byte>`

**File:** `Runtime/Performance/PooledArrayBufferWriter.cs` (new)

Required behavior:

1. Implement `IBufferWriter<byte>` backed by arrays rented from `ArrayPool<byte>.Shared`.
2. Support dynamic growth: when the current buffer is full, rent a new larger buffer (2x growth strategy), copy existing data, and return the old buffer.
3. Expose `WrittenMemory` property returning `ReadOnlyMemory<byte>` of the written portion.
4. Expose `WrittenSpan` property returning `ReadOnlySpan<byte>` of the written portion.
5. Expose `WrittenCount` (int) — number of bytes written so far.
6. Implement `IDisposable` — on dispose, return the backing buffer to the pool.
7. Provide a `Reset()` method that clears the write position without returning the buffer (for reuse across serialization calls).

Implementation constraints:

1. Initial buffer size should be configurable (default: 256 bytes) to minimize rent/copy overhead for small payloads.
2. Growth factor is 2x (double on each resize) to amortize copy cost.
3. Maximum buffer size should be bounded (default: 1MB) to prevent runaway growth — throw `InvalidOperationException` if exceeded.
4. `GetSpan(int sizeHint)` and `GetMemory(int sizeHint)` must handle `sizeHint == 0` (return remaining space) and `sizeHint > remaining` (trigger growth).
5. Thread safety: NOT thread-safe — `PooledArrayBufferWriter` is designed for single-threaded use within a serialization call. Document this.
6. Mark the class as `sealed` for IL2CPP devirtualization.

---

## Step 3: Update `MultipartFormDataBuilder` to Buffer-Based Output

**File:** `Runtime/Content/MultipartFormDataBuilder.cs` (modified)

Required behavior:

1. Replace the `Build()` method that returns `byte[]` via `MemoryStream` with a buffer-based `WriteTo(IBufferWriter<byte> output)` method.
2. `WriteTo` writes boundary delimiters, content-disposition headers, and part bodies directly into the provided `IBufferWriter<byte>`.
3. Part bodies that are `byte[]` or `ReadOnlyMemory<byte>` are written directly (no copy to intermediate buffer).
4. Part bodies that are `string` are encoded directly into the buffer writer via `Encoding.UTF8.GetBytes(str, span)`.
5. Maintain backward compatibility: keep `Build()` as a convenience method that internally creates a `PooledArrayBufferWriter`, calls `WriteTo`, and returns the result as `IMemoryOwner<byte>`.

Implementation constraints:

1. `WriteTo` must not allocate any intermediate byte arrays — all output goes directly to the `IBufferWriter<byte>`.
2. Boundary string and header lines should be cached as `byte[]` (pre-encoded at construction time) to avoid UTF-8 re-encoding per `WriteTo` call.
3. Ensure CRLF line endings are correctly written for multipart boundaries (RFC 2046).
4. The method must handle zero-length parts correctly.
5. When using the backward-compatible `Build()` wrapper, the `PooledArrayBufferWriter` must be disposed after extracting the result.

---

## Step 4: Integrate Buffer-Based Serialization into Request Pipeline

**File:** `Runtime/Core/UHttpClient.cs` (modified) or relevant request serializer

Required behavior:

1. When content body serialization is needed (JSON POST, PUT, PATCH), check if the serializer supports `IBufferWriter<byte>` (via `IBufferJsonSerializer` or interface check).
2. If buffer-based serialization is available AND `UseZeroAllocPipeline` is enabled:
   - Create a `PooledArrayBufferWriter` with an estimated initial size.
   - Serialize directly into the buffer writer.
   - Use the `WrittenMemory` as the request body.
   - Dispose the buffer writer after the request is sent.
3. If buffer-based serialization is NOT available, fall back to the existing string-based path (no regression).

Implementation constraints:

1. The fallback path must be the default — zero-allocation serialization is opt-in via `UseZeroAllocPipeline` flag AND a compatible serializer.
2. Do NOT change the public API signature of request methods — this is an internal optimization.
3. Initial buffer size estimation: use `Content-Length` if known, otherwise default to 256 bytes.
4. Ensure the `PooledArrayBufferWriter` is always disposed via try/finally, even on serialization failure.

---

## Verification Criteria

1. `IJsonSerializer` extensions compile and are backward-compatible with existing implementations.
2. `LiteJsonSerializer` correctly implements the bridge methods (serialize to string → encode to buffer).
3. `PooledArrayBufferWriter` correctly grows on demand and returns buffers on dispose.
4. `PooledArrayBufferWriter` handles `GetSpan(0)`, `GetSpan(largeValue)`, and `Advance(count)` correctly.
5. `MultipartFormDataBuilder.WriteTo` produces byte-identical output to the original `Build()` method.
6. Request pipeline correctly uses buffer-based serialization when `UseZeroAllocPipeline = true` and the serializer supports it.
7. Memory profiler shows elimination of `MemoryStream` and intermediate `byte[]` allocations in the serialization path.
8. No regression in serialization correctness for all content types (JSON, multipart, form-urlencoded).
