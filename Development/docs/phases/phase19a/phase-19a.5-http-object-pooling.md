# Phase 19a.5: HTTP Object Pooling

**Depends on:** Phase 19 (Async Runtime Refactor)
**Estimated Effort:** 2-3 days

---

## Step 0: Pool `UHttpResponse` Objects

**File:** `Runtime/Core/UHttpResponse.cs` (modified)
**File:** `Runtime/Performance/HttpResponsePool.cs` (new)

Required behavior:

1. Create an `HttpResponsePool` static class that manages `UHttpResponse` instances via the existing `ObjectPool<T>`.
2. `Rent()` returns a pre-allocated `UHttpResponse` with all fields reset to defaults.
3. `Return(UHttpResponse response)` resets state and returns to pool:
   - Clear `StatusCode`, `ReasonPhrase`.
   - Clear headers dictionary (`.Clear()`, do NOT recreate).
   - Return body buffer to `ArrayPool<byte>` if pooled.
   - Clear `Body`, `BodyBytes`, `BodyString` cached values.
   - Reset error state and internal flags.
4. `UHttpResponse` must implement a `Reset()` method for the above operations.
5. Pool size: configurable, default 64 instances.

Implementation constraints:

1. `Reset()` must clear ALL fields — stale data from a previous response is a security bug.
2. Uses existing `ObjectPool<T>` reset callback mechanism.
3. Gated behind `UseZeroAllocPipeline`.
4. Body buffer lifecycle: if backed by `IMemoryOwner<byte>`, `Reset()` must dispose it.

---

## Step 1: Pool `Http2Stream` Objects

**File:** `Runtime/Transport/Http2/Http2Stream.cs` (modified)
**File:** `Runtime/Transport/Http2/Http2StreamPool.cs` (new)

Required behavior:

1. Create `Http2StreamPool` managing `Http2Stream` instances via `ObjectPool<T>`.
2. `Rent(int streamId)` returns a reset `Http2Stream` with the new stream ID.
3. `Return(Http2Stream stream)` resets per-stream state:
   - Clear stream ID, state flags, flow control window.
   - Clear request/response references.
   - Return pending data buffers to `ArrayPool`.
   - Reset `ValueTaskSource` state.
4. Pool size: configurable, default 128 (HTTP/2 default concurrent streams is 100).

Implementation constraints:

1. Stream ID assignment at rent time, not construction.
2. `Reset()` must handle partial states (stream returned before response complete).
3. Window size reset to connection's initial window size.
4. Thread-safe — streams rented/returned from different threads.

---

## Step 2: Pool Header Dictionary Collections

**File:** `Runtime/Performance/HeaderDictionaryPool.cs` (new)

Required behavior:

1. Pool `Dictionary<string, string>` instances for HTTP headers.
2. `Rent()` returns a cleared dictionary. `Return()` calls `.Clear()` and returns to pool.
3. Pool size: configurable, default 128.
4. Dictionaries use `StringComparer.OrdinalIgnoreCase` for HTTP header semantics.

Implementation constraints:

1. `Clear()` retains internal bucket array — desired behavior (avoids reallocation).
2. Initial capacity: 16 (typical 8-15 headers per response).
3. Integrated with `UHttpResponse.Reset()` — header dictionary returned alongside response.
4. Sensitive headers (Authorization, Cookie) cleared by `Clear()`.

---

## Step 3: Pool `HpackEncoder` Internal State

**File:** `Runtime/Transport/Http2/HpackEncoder.cs` (modified)

Required behavior:

1. Pool the `BufferWriter` internal state used during HPACK encoding.
2. Rent from pool before encode, return after encoding complete.
3. Reset buffer writer state (clear position, reset capacity) on return.

Implementation constraints:

1. Return in `finally` block even on encoding failure.
2. This pools the writer OBJECT, not the backing array (handled by 19a.1).
3. Stateless encoder: straightforward pool integration.

---

## Verification Criteria

1. `UHttpResponse` pool rents and returns without data leaks between requests.
2. `Reset()` clears ALL fields — unit tests assert every property is default after reset.
3. `Http2Stream` pool handles stream ID reassignment and partial state cleanup.
4. Header dictionary pool returns cleared dictionaries with case-insensitive comparison.
5. `HpackEncoder` state pooling does not affect encoding correctness.
6. Pool diagnostic counters show expected rent/return/miss patterns.
7. No security data leaks — auth headers, cookies, bodies cleared on return.
8. All existing tests pass with `UseZeroAllocPipeline = true`.
9. Memory profiler shows elimination of per-request object allocations under sustained load.
10. Under 10,000 RPS benchmark, GC pause time is measurably reduced.
