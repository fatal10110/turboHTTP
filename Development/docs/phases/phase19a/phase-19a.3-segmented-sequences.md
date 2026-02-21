# Phase 19a.3: Segmented Sequences (`ReadOnlySequence<byte>`)

**Depends on:** 19a.1 (`ArrayPool<byte>` Completion Sweep)
**Estimated Effort:** 1 week

---

## Step 0: Implement `PooledSegment` and `SegmentedBuffer`

**File:** `Runtime/Performance/PooledSegment.cs` (new)
**File:** `Runtime/Performance/SegmentedBuffer.cs` (new)

Required behavior:

1. **`PooledSegment`** — A `ReadOnlySequenceSegment<byte>` subclass that wraps a pooled `byte[]` from `ArrayPool<byte>.Shared`:
   - Stores the rented array and the actual written length.
   - `Memory` property returns `ReadOnlyMemory<byte>` sliced to the written length.
   - Provides `SetNext(PooledSegment)` to link segments into a chain.
   - Implements `IDisposable` to return the array to the pool.

2. **`SegmentedBuffer`** — A builder that manages a linked list of `PooledSegment` instances:
   - `Write(ReadOnlySpan<byte> data)` — appends data to the current segment; if the segment is full, rents a new one and links it.
   - `GetSequence()` — returns a `ReadOnlySequence<byte>` spanning all segments.
   - `TotalLength` (long) — total bytes written across all segments.
   - `Dispose()` — returns all rented segments to the pool.
   - `Reset()` — returns all segments except the first, resets write position (for reuse).
3. Each segment has a fixed size (configurable, default: 16KB) to avoid LOH allocations (LOH threshold is ~85KB).

Implementation constraints:

1. Segment chain must form a valid `ReadOnlySequence<byte>` — first segment's `RunningIndex` is 0; each subsequent segment's `RunningIndex` is the sum of all previous segments' lengths.
2. `PooledSegment` must extend `ReadOnlySequenceSegment<byte>` (the .NET base class for multi-segment sequences).
3. Thread safety: NOT thread-safe — designed for single-writer, single-reader use within a connection's receive loop.
4. Mark both classes as `sealed` for IL2CPP devirtualization.
5. Segment size should be a power-of-two multiple of page size for optimal memory alignment.

---

## Step 1: Implement `SegmentedReadStream`

**File:** `Runtime/Performance/SegmentedReadStream.cs` (new)

Required behavior:

1. Create a `SegmentedReadStream : Stream` wrapper that allows `DeflateStream` (and other `Stream`-based consumers) to read from a `ReadOnlySequence<byte>` input.
2. The stream reads sequentially across segment boundaries — when one segment is exhausted, it advances to the next.
3. `Read(byte[] buffer, int offset, int count)` — synchronous read, copies data from the current segment position into the provided buffer.
4. `ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)` — async version (synchronous internally since all data is in memory, but must return `Task<int>` for API compatibility).
5. Support `Position` (get/set) and `Length` properties.
6. Support `Seek` for full `Stream` compatibility (required by some decompression libraries).

Implementation constraints:

1. The stream does NOT own the segments — it reads from a `ReadOnlySequence<byte>` provided at construction time. The caller manages segment lifecycle.
2. Do NOT copy the entire sequence into a contiguous buffer — that defeats the purpose. Read directly from segment memory.
3. `ReadAsync` is synchronous (returns `Task.FromResult`) since the data is already in memory. This avoids async state machine allocation for in-memory reads.
4. Track current position via a `SequencePosition` field to efficiently resume reads across segments.
5. Mark `CanRead = true`, `CanSeek = true`, `CanWrite = false`. Write methods throw `NotSupportedException`.

---

## Step 2: Integrate Segmented Sequences into HTTP Chunked Transfer

**File:** `Runtime/Transport/Http11ResponseParser.cs` (modified) or equivalent response body reader

Required behavior:

1. When reading chunked HTTP response bodies, instead of allocating a growing contiguous buffer:
   - Rent a 16KB `PooledSegment` for the first chunk.
   - If the chunk exceeds 16KB, rent additional segments and link them.
   - Each HTTP chunk boundary is handled by the reader; data may span segment boundaries.
2. The final response body is represented as a `ReadOnlySequence<byte>` via `SegmentedBuffer.GetSequence()`.
3. If decompression is needed (Content-Encoding: gzip/deflate):
   - Wrap the segmented input in a `SegmentedReadStream`.
   - Pass to `DeflateStream` / `GZipStream` for decompression.
   - Decompression output is written into a **new** `SegmentedBuffer` (segmented output, not contiguous).
4. This is gated behind `UseZeroAllocPipeline` — when disabled, use the existing contiguous buffer path.

Implementation constraints:

1. Chunk boundaries in chunked transfer encoding may not align with segment boundaries — the reader must handle data spanning across segments correctly.
2. The `SegmentedReadStream` adapter must provide correct `Read()` results even when a single `Read()` call spans multiple segments.
3. If `SegmentedStream` performance proves insufficient for the decompression subsystem, document the issue and fall back to flattening with a pooled buffer — but this must be measured and justified.
4. The existing response body API (`UHttpResponse.Body`, `.BodyBytes`, `.BodyString`) must continue to work. For the segmented path, provide lazy flattening: `BodyBytes` flattens the sequence on first access and caches the result.
5. Do NOT change the public `UHttpResponse` API — add an internal `BodySequence` property for internal consumers that can process segmented data.

---

## Step 3: Integrate Segmented Sequences into WebSocket Message Assembly

**File:** `Runtime/WebSocket/MessageAssembler.cs` (modified)

Required behavior:

1. When assembling fragmented WebSocket messages, instead of copying all fragments into a single contiguous buffer:
   - Each fragment's payload buffer (already rented from the pool) becomes a segment in a `SegmentedBuffer`.
   - Fragments are linked as segments without copying.
   - The assembled message payload is represented as a `ReadOnlySequence<byte>`.
2. For `permessage-deflate` decompression (if implemented):
   - Wrap the segmented payload in `SegmentedReadStream`.
   - Decompress into a new `SegmentedBuffer`.
3. For text messages requiring UTF-8 validation, validate across segment boundaries (multi-byte UTF-8 characters may span segments).
4. This is gated behind `UseZeroAllocPipeline` — when disabled, use the existing contiguous copy path.

Implementation constraints:

1. Fragment ownership transfers to the `SegmentedBuffer` — individual fragment buffers must NOT be returned to the pool until the assembled message is disposed.
2. UTF-8 validation across segment boundaries requires a `Utf8SegmentValidator` that tracks partial character state between segments. Use `System.Text.Encoding.UTF8.GetDecoder()` with `flush: false` for intermediate segments and `flush: true` for the final segment.
3. `WebSocketMessage.Data` (the public property) must continue to return contiguous data. For the segmented path, flatten lazily on first access — or provide an alternative `DataSequence` property.
4. The `MaxMessageSize` check must still be enforced during accumulation — check total segment length before linking each new fragment.

---

## Verification Criteria

1. `PooledSegment` correctly wraps pooled arrays and forms valid `ReadOnlySequence<byte>` chains.
2. `SegmentedBuffer` correctly manages segment lifecycle (rent, link, dispose).
3. `SegmentedReadStream` correctly reads across segment boundaries without data corruption.
4. `SegmentedReadStream.Seek` works correctly for arbitrary positions.
5. Chunked HTTP response bodies spanning multiple segments are correctly assembled.
6. Decompression via `SegmentedReadStream` produces identical output to the contiguous buffer path.
7. WebSocket fragmented message assembly via segments produces identical payloads to the contiguous copy path.
8. UTF-8 validation works correctly for multi-byte characters spanning segment boundaries.
9. No LOH allocations (> 85KB contiguous) during large message processing.
10. Memory profiler shows segmented path eliminates growing-buffer copy allocations.
11. `BodyBytes` lazy flattening works correctly and caches the result.
12. All existing tests pass with `UseZeroAllocPipeline = true` and `UseZeroAllocPipeline = false`.
