# Phase 19a.2: Buffer-Writer-First Serialization

**Depends on:** 19a.0, 19a.1
**Estimated Effort:** 1 week

---

## Step 0: Extend `IJsonSerializer` for Buffer/Sequence I/O

**File:** `Runtime/JSON/IJsonSerializer.cs` (modified)

Required behavior:

1. Add buffer/sequence methods directly to `IJsonSerializer`:
   ```csharp
   void Serialize<T>(T value, IBufferWriter<byte> output);
   T Deserialize<T>(ReadOnlySequence<byte> input);
   ```
2. Treat this as a planned greenfield contract change (no migration compatibility layer).
3. Update all in-repo serializer implementations.

Implementation constraints:

1. No DIM fallback branch in this phase.
2. Document that serializer implementations should write directly to provided buffers.

---

## Step 1: Update `LiteJsonSerializer` Bridge Implementation

**File:** `Runtime/JSON/LiteJson/LiteJsonSerializer.cs` (modified)

Required behavior:

1. Implement new methods with a bridge path:
   - Serialize to JSON string.
   - Encode directly into `IBufferWriter<byte>` span/memory.
2. Implement sequence-based deserialize handling single and multi-segment input.

Implementation constraints:

1. Avoid intermediate `byte[]` allocations where possible.
2. Preserve existing exception model.

---

## Step 2: Create `PooledArrayBufferWriter`

**File:** `Runtime/Performance/PooledArrayBufferWriter.cs` (new)

Required behavior:

1. Implement `IBufferWriter<byte>` backed by `ArrayPool<byte>.Shared`.
2. Support growth and expose `WrittenMemory`, `WrittenSpan`, `WrittenCount`.
3. Add explicit ownership transfer API:
   - `DetachAsOwner()` (or equivalent) returns an `IMemoryOwner<byte>` over the written data.
   - After detach, writer becomes unusable until reset/reinitialized.
4. Implement `Reset()` for reuse and `Dispose()` for return-to-pool.

Implementation constraints:

1. Ownership transfer must prevent use-after-return and double-return.
2. Enforce maximum buffer size.
3. Keep class `sealed` and non-thread-safe (documented).

---

## Step 3: Introduce Request Body Ownership Semantics

**Files modified:**
- `Runtime/Core/UHttpRequest.cs`
- `Runtime/Core/UHttpRequestBuilder.cs`
- `Runtime/Transport/RawSocketTransport.cs`

Required behavior:

1. Add internal support for leased/pool-owned request bodies.
2. Ensure body owners are disposed exactly once after send completion/failure.
3. Preserve current external request-building ergonomics.

Implementation constraints:

1. No body-owner leaks on cancellation, timeout, or transport exceptions.
2. Existing `byte[]` body path remains supported.

---

## Step 4: Move JSON + Multipart to Writer-Based Production

**Files modified:**
- `Runtime/JSON/JsonRequestBuilderExtensions.cs`
- `Runtime/Files/MultipartFormDataBuilder.cs`

Required behavior:

1. JSON request helpers write through `IBufferWriter<byte>` path by default.
2. `MultipartFormDataBuilder` gains `WriteTo(IBufferWriter<byte>)`.
3. `Build()` returns owned pooled data (`IMemoryOwner<byte>`) or equivalent leased-body output.
4. `ApplyTo(UHttpRequestBuilder)` uses leased body path instead of forcing `byte[]` copy.

Implementation constraints:

1. Remove the prior `Build() + MemoryStream + ToArray()` allocation chain.
2. Maintain exact multipart wire format correctness (CRLF, boundaries, headers).

---

## Step 5: Integrate Into Send Pipeline

**Files modified:**
- `Runtime/Core/UHttpClient.cs`
- `Runtime/Transport/RawSocketTransport.cs`

Required behavior:

1. Ensure leased request body lifetime is tied to request execution.
2. Guarantee deterministic cleanup in success/failure/cancellation cases.
3. Keep middleware and interceptor behavior unchanged.

Implementation constraints:

1. No transport behavior regression.
2. No leaked pooled arrays under repeated send failures.

---

## Verification Criteria

1. `IJsonSerializer` contract update compiles across all runtime implementations.
2. `PooledArrayBufferWriter` ownership transfer is safe and leak-free.
3. JSON and multipart request creation avoid intermediate `byte[]` copies.
4. Request-body owner cleanup is correct in all terminal states.
5. Existing request serialization tests continue to pass.
