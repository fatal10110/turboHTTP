# Phase 19a.0: Safety Infrastructure & Debug Guards

**Depends on:** None (foundational)
**Estimated Effort:** 2-3 days

---

## Step 0: Add Zero-Allocation Feature Flag

**File:** `Runtime/Core/TurboHttpConfig.cs` (modified)

Required behavior:

1. Add a `UseZeroAllocPipeline` boolean property (default: `false`) to `TurboHttpConfig`.
2. When `false`, all zero-allocation codepaths introduced in Phase 19a are bypassed — the library uses the original proven paths.
3. When `true`, the zero-allocation codepaths are active (pooled buffers, segmented sequences, SAEA transport, etc.).
4. This flag gates codepaths at the **transport and serialization layers** — middleware and public API surface remain unchanged regardless of flag state.

Implementation constraints:

1. The flag must be settable before `TurboHttpClient` is constructed and immutable afterwards (config freeze pattern already present in `TurboHttpConfig`).
2. Document the flag with XML comments explaining its purpose, recommended usage (enable after testing in development), and that it is experimental during alpha.
3. Do NOT gate individual sub-phase features separately — `UseZeroAllocPipeline` is a single master switch. Sub-phase-specific configuration (e.g., `TransportMode.Saea`) remains independent.

---

## Step 1: Implement PooledBuffer Debug Wrapper

**File:** `Runtime/Performance/PooledBufferDebug.cs` (new)

Required behavior:

1. Create a `PooledBuffer<T>` debug wrapper that wraps `IMemoryOwner<T>` and is only active when `#if DEBUG` or `TURBOHTTP_POOL_DIAGNOSTICS` is defined.
2. Detect **use-after-return**: accessing `Memory` or `Span` properties after the buffer has been returned to the pool must throw `ObjectDisposedException` with a descriptive message including the original allocation callsite.
3. Detect **double-return**: returning the same buffer instance twice must throw `InvalidOperationException`. Track return state with a boolean flag set on `Dispose()`.
4. Detect **buffer overrun from pooled-array-larger-than-requested**: validate that consumers only access the originally requested slice length, not the full pooled array length.
   - Store the requested length at construction time.
   - `Memory` and `Span` properties return sliced views of the underlying array using `Memory<T>.Slice(0, requestedLength)`.
   - In debug mode, poison bytes beyond the requested length (fill with `0xCD`) on rent to help detect overruns in memory dumps.
5. Capture the allocation stack trace (via `Environment.StackTrace`) at construction time for diagnostic messages. This is debug-only — zero overhead in release builds.

Implementation constraints:

1. In release builds (no `DEBUG` or `TURBOHTTP_POOL_DIAGNOSTICS`), `PooledBuffer<T>` must be a zero-overhead thin wrapper — inline `Memory` access, no tracking, no stack trace capture.
2. Use `#if` conditional compilation, not runtime checks, to eliminate debug overhead.
3. Implement `IMemoryOwner<T>` and `IDisposable` interfaces.
4. Mark the class as `sealed` for devirtualization by IL2CPP.
5. The wrapper must work with both `ArrayPool<byte>.Shared` and the existing `ByteArrayPool` facade.

---

## Step 2: Add Pool Diagnostic Counters

**File:** `Runtime/Performance/ByteArrayPool.cs` (modified)
**File:** `Runtime/Performance/ObjectPool.cs` (modified)

Required behavior:

1. Add diagnostic counters to `ByteArrayPool`:
   - `RentCount` (long) — total number of buffers rented.
   - `ReturnCount` (long) — total number of buffers returned.
   - `MissCount` (long) — total number of rents where the pool was empty and a new array was allocated.
   - `ActiveCount` (computed: `RentCount - ReturnCount`) — currently outstanding buffers.
2. Add the same diagnostic counters to `ObjectPool<T>`:
   - `RentCount`, `ReturnCount`, `MissCount`, `ActiveCount` with the same semantics.
3. Expose counters via a `PoolDiagnostics` struct returned from a `GetDiagnostics()` method on each pool.
4. Add a static `ResetDiagnostics()` method to clear all counters (useful between benchmark iterations).

Implementation constraints:

1. Counters must use `Interlocked.Increment` for thread-safe updates — no locks.
2. Counters are always active (not gated by `#if DEBUG`) because the overhead of `Interlocked.Increment` is negligible (< 1ns per operation on modern CPUs).
3. `PoolDiagnostics` must be a `readonly struct` to avoid boxing allocations when passed around.
4. Do NOT change existing pool behavior — counters are purely additive instrumentation.
5. Counter values must be `long` (not `int`) to avoid overflow in long-running applications.

---

## Step 3: Add Pool Health Logging

**File:** `Runtime/Performance/PoolHealthReporter.cs` (new)

Required behavior:

1. Create a `PoolHealthReporter` utility that periodically logs pool diagnostic summaries.
2. Log entries include: pool name, rent/return/miss counts, active buffer count, miss rate percentage.
3. Reporting interval is configurable (default: 60 seconds).
4. Output via existing TurboHTTP logging infrastructure (not `Debug.Log` directly).
5. Can be enabled/disabled via `TurboHttpConfig.EnablePoolDiagnosticLogging` (default: `false`).

Implementation constraints:

1. Reporting must be non-blocking — use a timer callback, not a dedicated thread.
2. The reporter must handle multiple pool instances (ByteArrayPool, ObjectPool instances).
3. Logging must not allocate strings in the hot path — format only on log output.
4. The reporter must be `IDisposable` to clean up the timer on shutdown.
5. Use `System.Threading.Timer` for the periodic callback (not Unity-specific timers) for cross-platform compatibility.

---

## Verification Criteria

1. `PooledBuffer<T>` debug mode correctly throws `ObjectDisposedException` on use-after-return.
2. `PooledBuffer<T>` debug mode correctly throws `InvalidOperationException` on double-return.
3. `PooledBuffer<T>` slicing validates that consumers cannot access bytes beyond the requested length.
4. Poison bytes (`0xCD`) are written beyond the requested length in debug builds.
5. In release builds, `PooledBuffer<T>` has zero overhead (no stack trace capture, no tracking state).
6. Pool diagnostic counters accurately reflect rent/return/miss operations under concurrent access.
7. `PoolHealthReporter` produces correct summary output at configured intervals.
8. `UseZeroAllocPipeline` flag correctly gates zero-allocation codepaths without affecting existing behavior.
9. All existing tests continue to pass with `UseZeroAllocPipeline = false`.
