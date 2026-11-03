# Phase 19a.3: Segmented Sequences (`ReadOnlySequence<byte>`)

**Depends on:** 19a.1 (`ArrayPool<byte>` completion)
**Estimated Effort:** 1 week

---

## Step 0: Implement `PooledSegment` + `SegmentedBuffer`

**File:** `Runtime/Performance/PooledSegment.cs` (new)
**File:** `Runtime/Performance/SegmentedBuffer.cs` (new)

Required behavior:

1. `PooledSegment` extends `ReadOnlySequenceSegment<byte>` and owns one pooled array slice.
2. `SegmentedBuffer` manages append, sequence projection, reset, and dispose.
3. Default segment size is 16KB (LOH-safe).
4. `Dispose()` returns all arrays deterministically.

Implementation constraints:

1. Running indexes must produce valid multi-segment `ReadOnlySequence<byte>`.
2. Classes are `sealed` and single-writer/single-reader by design.

---

## Step 1: Implement `SegmentedReadStream`

**File:** `Runtime/Performance/SegmentedReadStream.cs` (new)

Required behavior:

1. Add a `Stream` adapter over `ReadOnlySequence<byte>`.
2. Correctly read across segment boundaries.
3. Provide `Read`, `ReadAsync`, `Seek`, `Position`, and `Length`.
4. Never flatten entire sequence just to satisfy reads.

Implementation constraints:

1. The stream does not own segment lifetimes.
2. `ReadAsync` may complete synchronously for in-memory data.

---

## Step 2: Integrate Into HTTP/1.1 Chunked + Decompression Paths

**File:** `Runtime/Transport/Http1/Http11ResponseParser.cs` (modified)

Required behavior:

1. Chunked-body assembly uses `SegmentedBuffer` (no growing contiguous arrays).
2. Decompression input/output use segmented adapters.
3. Keep all existing chunk-size and body-limit safety checks.

Implementation constraints:

1. Correct handling when chunk boundaries and segment boundaries do not align.
2. Keep protocol error mapping unchanged.

---

## Step 3: Add Sequence-Aware Response Body Handling

**File:** `Runtime/Core/UHttpResponse.cs` (modified)

Required behavior:

1. Add internal segmented-body representation (`ReadOnlySequence<byte>` or equivalent).
2. Keep public body APIs functional.
3. Flatten lazily only when callers require contiguous data.
4. Cache flattened data to avoid repeated copies.

Implementation constraints:

1. Public API behavior stays deterministic.
2. Disposal returns any pooled segmented storage.

---

## Step 4: Integrate Into WebSocket Fragment Assembly

**Files modified:**
- `Runtime/WebSocket/MessageAssembler.cs`
- `Runtime/WebSocket/WebSocketMessage.cs`

Required behavior:

1. Fragmented payloads are assembled as linked segments without mandatory contiguous copy.
2. UTF-8 validation for text messages supports multi-byte sequences across boundaries.
3. Keep max message size enforcement during accumulation.
4. Provide contiguous flattening only when required by existing `Data` access pattern.

Implementation constraints:

1. Ownership transfer from fragment buffers to assembled message is explicit.
2. All pooled buffers are returned on reset/dispose/error.

---

## Verification Criteria

1. Segment chain integrity is valid and leak-free.
2. HTTP chunked/decompression outputs match prior behavior.
3. WebSocket fragmented message payloads are byte-identical to prior behavior.
4. UTF-8 boundary validation remains correct.
5. Large-message processing avoids LOH growth from contiguous expansion.
