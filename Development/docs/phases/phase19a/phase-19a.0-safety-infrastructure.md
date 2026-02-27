# Phase 19a.0: Runtime Safety Infrastructure & Diagnostics

**Depends on:** None (foundational)
**Estimated Effort:** 2-3 days

---

## Step 1: Implement `PooledBuffer<T>` Debug Guard Wrapper

**File:** `Runtime/Performance/PooledBufferDebug.cs` (new)

Required behavior:

1. Add a `PooledBuffer<T>` wrapper over `IMemoryOwner<T>`.
2. In `DEBUG` or `TURBOHTTP_POOL_DIAGNOSTICS` builds, detect:
   - use-after-return (`ObjectDisposedException`)
   - double-return (`InvalidOperationException`)
   - requested-length violations (return only sliced `Memory`/`Span`)
3. Capture allocation stack traces in debug builds for diagnostics.
4. In release builds, keep the wrapper effectively zero-overhead.

Implementation constraints:

1. Use compile-time conditionals (`#if`) for debug features.
2. Keep class `sealed`.
3. Implement `IMemoryOwner<T>` and `IDisposable`.

---

## Step 2: Add Pool Diagnostics (Feasible Metrics)

**File:** `Runtime/Performance/ByteArrayPool.cs` (modified)
**File:** `Runtime/Performance/ObjectPool.cs` (modified)

Required behavior:

1. Add diagnostics to `ByteArrayPool`:
   - `RentCount`
   - `ReturnCount`
   - `ClearOnReturnCount`
   - `ActiveCount` (`RentCount - ReturnCount`)
2. Add diagnostics to `ObjectPool<T>`:
   - `RentCount`
   - `ReturnCount`
   - `MissCount` (factory-created instances)
   - `ActiveCount` (`RentCount - ReturnCount`)
3. Expose diagnostics via readonly structs and `GetDiagnostics()`.
4. Add reset methods for benchmark/test isolation.

Implementation constraints:

1. Use `Interlocked` for counter updates.
2. Diagnostics must be additive and behavior-preserving.
3. Do not claim ByteArrayPool misses unless the implementation can measure them accurately.

---

## Step 3: Add Pool Health Reporter

**File:** `Runtime/Observability/PoolHealthReporter.cs` (new)

Required behavior:

1. Add a timer-based reporter for periodic pool diagnostics.
2. Output pool name, active count, miss rate (where applicable), and rent/return deltas.
3. Integrate with existing logging path.
4. Gate via `UHttpClientOptions` opt-in diagnostic setting.

Implementation constraints:

1. Use `System.Threading.Timer`.
2. Implement `IDisposable` for cleanup.
3. Keep reporting off the request hot path.

---

## Verification Criteria

1. Debug wrapper catches use-after-return and double-return defects.
2. Requested-length slicing is enforced in diagnostics mode.
3. ByteArrayPool/ObjectPool diagnostics are correct under concurrency.
4. Pool health logging is stable under sustained traffic.
