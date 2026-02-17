# Phase 6.1: Pooling Primitives (ObjectPool + ByteArrayPool)

**Depends on:** Phase 5 (Content Handlers)
**Assembly:** `TurboHTTP.Performance`
**Files:** 2 new

---

## Step 1: Create Generic `ObjectPool<T>`

**File:** `Runtime/Performance/ObjectPool.cs`

Required behavior:

1. `Rent()` returns pooled instances when available, otherwise creates a new one.
2. `Return()` applies optional reset callback before adding back to pool.
3. Pool enforces `maxSize` with atomic cap-check semantics (no race between check and insert).
4. `Clear()` removes all pooled items.
5. `Count` is explicitly approximate (snapshot-style), not exact under contention.

---

## Step 2: Create `ByteArrayPool`

**File:** `Runtime/Performance/ByteArrayPool.cs`

Required behavior:

1. `Rent(minimumSize)` returns at least the requested size.
2. Prefer standard bucket sizes (1KB, 4KB, 8KB, etc.).
3. `minimumSize <= 0` throws `ArgumentOutOfRangeException`.
4. `Return(buffer)` follows explicit oversized policy:
   - pool up to `1MB` bucketed sizes
   - discard buffers larger than `1MB` (explicitly unpooled)
5. Returned pooled buffers are reset before reuse (clear-on-rent or equivalent).
6. Buckets have explicit max occupancy (default `32` arrays per bucket).
7. `Clear()` resets all buckets.

Implementation constraints:

1. Use explicit thread-safe collection strategy:
   - per-size bucket storage: `ConcurrentQueue<byte[]>`
   - cap tracking: `Interlocked` counter per bucket
   - deterministic bounded behavior over implicit bag growth
   - if IL2CPP profiling shows unacceptable `ConcurrentQueue` node allocation overhead, allow lock-based bounded `Queue<byte[]>` fallback behind internal option
2. Avoid per-rent allocations in the steady state.
3. Keep bucket selection deterministic.
4. Validate behavior on IL2CPP/AOT builds (pool semantics must match Editor/Mono behavior).
5. Reset callback requirements:
   - thread-safe and reentrant
   - no blocking work
   - exceptions discard item instead of returning it to pool
6. Safety mode uses full-buffer zeroing (`Array.Clear(0, Length)`) before reuse.

---

## Verification Criteria

1. `ObjectPool<T>` rent/return/clear behavior works under contention.
2. `ByteArrayPool` reuses buffers for standard sizes.
3. Power-of-two/oversize behavior is explicit and tested.
4. Memory churn is lower than non-pooled baseline in repeated rent/return loops.
5. No data leakage across pooled buffer reuse.
6. No exceptions for null returns or unsupported-size return paths.
7. Oversized (>1MB) buffers are verified as intentionally unpooled.
8. IL2CPP profiling verifies queue-node allocation overhead remains within Phase 6 budget (or fallback path is used).
