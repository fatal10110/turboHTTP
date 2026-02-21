# Phase 19.3: HTTP/2 Hot-Path Refactor

**Depends on:** 19.1 (ValueTask Migration)
**Estimated Effort:** 1 week

---

## Step 0: IL2CPP AOT Smoke Test for IValueTaskSource

**Goal:** Validate that `ManualResetValueTaskSourceCore<T>` and `IValueTaskSource<T>` work correctly under IL2CPP AOT compilation before committing to the implementation.

Required behavior:

1. Create a minimal test project (or test case within the existing test suite) that:
   - Implements `IValueTaskSource<bool>` backed by `ManualResetValueTaskSourceCore<bool>`.
   - Creates a `ValueTask<bool>` from the custom source.
   - Awaits the `ValueTask` after setting the result on the source.
   - Verifies correct result delivery.
2. Build the test with IL2CPP AOT on iOS (or Android if iOS is unavailable).
3. Run the test on a real device or simulator.
4. Document the result: PASS (proceed with `IValueTaskSource` implementation) or FAIL (use fallback strategy).

Implementation constraints:

1. `ManualResetValueTaskSourceCore<T>` is a mutable struct with complex generic instantiation — IL2CPP may struggle with the combination of generic virtual dispatch + struct layout.
2. Test with at least two generic instantiations: `IValueTaskSource<bool>` and `IValueTaskSource<UHttpResponse>` to cover both primitive and class type arguments.
3. **Fallback strategy (if IL2CPP fails):** Pool `TaskCompletionSource<T>` objects via `ObjectPool<T>` instead. Less optimal (still allocates the `Task<T>` wrapper) but safe and proven. Document in this step whether the fallback is activated.

---

## Step 1: Implement Poolable ValueTaskSource for HTTP/2 Streams

**Goal:** Replace per-operation `TaskCompletionSource<UHttpResponse>` allocations in `Http2Stream` with a poolable `IValueTaskSource<UHttpResponse>` implementation.

Required behavior:

1. Create `PoolableResponseSource` class implementing `IValueTaskSource<UHttpResponse>`.
2. Back it with `ManualResetValueTaskSourceCore<UHttpResponse>` for the core state machine.
3. Implement the `IValueTaskSource<UHttpResponse>` contract:
   - `GetResult(short token)` — returns the result and resets the source for reuse.
   - `GetStatus(short token)` — returns pending/succeeded/faulted.
   - `OnCompleted(Action<object> continuation, object state, short token, ValueTaskSourceOnCompletedFlags flags)` — registers the continuation.
4. Integrate with `ObjectPool<PoolableResponseSource>` for reuse — rent on stream creation, return on completion.
5. The source must support setting a result (`SetResult`), setting an exception (`SetException`), and setting cancellation (`SetCancellation`).

Implementation constraints:

1. `ManualResetValueTaskSourceCore<T>` must be stored as a field (not property) since it's a mutable struct.
2. The `Reset()` method must be called before returning to the pool — `ManualResetValueTaskSourceCore.Reset()` increments the version token, preventing stale awaits.
3. Thread safety: `SetResult` / `SetException` may be called from the HTTP/2 frame reader thread while `OnCompleted` is called from the requester thread — `ManualResetValueTaskSourceCore` handles this internally.
4. The pool should have a reasonable maximum size (e.g., 256) — HTTP/2 max concurrent streams per connection is typically 100-256.
5. If the IL2CPP smoke test (Step 0) fails, implement this step using `ObjectPool<TaskCompletionSource<UHttpResponse>>` instead, and skip the `IValueTaskSource` pattern.

---

## Step 2: Replace Http2Stream.ResponseTcs

**Goal:** Replace the `TaskCompletionSource<UHttpResponse>` in `Http2Stream` with the poolable `ValueTask` source from Step 1.

Required behavior:

1. Remove the `TaskCompletionSource<UHttpResponse>` field from `Http2Stream`.
2. Replace with a `PoolableResponseSource` rented from the pool.
3. Update `Http2Stream` to create `ValueTask<UHttpResponse>` from the poolable source: `new ValueTask<UHttpResponse>(source, source.Token)`.
4. Update the response completion path (when HEADERS + DATA frames arrive for the stream) to call `source.SetResult(response)`.
5. Update the error/cancellation paths (RST_STREAM, GOAWAY, timeout) to call `source.SetException(...)` or `source.SetCancellation(...)`.
6. Return the source to the pool after the `ValueTask` is consumed.

Implementation constraints:

1. The `ValueTask` must be consumed (awaited) exactly once — after consumption, the source is returned to the pool. Double-await would observe a reset or different-token source.
2. Source return timing: return to pool in `GetResult()` (called when the `ValueTask` is awaited and completes) — this ensures the source is returned immediately after consumption.
3. Cancellation path must still call `SetException` or `SetCancellation` on the source — do not just abandon it, or the pool will leak.
4. Verify that stream reset (RST_STREAM) correctly completes the source before returning it.

---

## Step 3: Replace Http2Connection Settings Ack TCS

**Goal:** Replace the `TaskCompletionSource` used for SETTINGS acknowledgment in `Http2Connection` with a poolable `ValueTask` source.

Required behavior:

1. Identify `Http2Connection._settingsAckTcs` (or equivalent) — the `TaskCompletionSource` used to await SETTINGS frame acknowledgment during connection setup and settings changes.
2. Replace with a `PoolableSettingsAckSource` implementing `IValueTaskSource<bool>` (or similar simple type).
3. Update the SETTINGS send path to create `ValueTask` from the source.
4. Update the SETTINGS ACK receive path to call `SetResult(true)` on the source.
5. Handle timeout: if no SETTINGS ACK within the connection timeout, call `SetException` with a timeout error.

Implementation constraints:

1. SETTINGS ACK is infrequent (once per connection setup, occasionally for settings updates) — this optimization has lower ROI than stream response TCS. Implement for consistency.
2. The same `PoolableResponseSource` pool can be reused if the type parameter matches, or create a separate `PoolableValueSource<bool>` if needed.
3. Consider whether a generic `PoolableValueTaskSource<T>` is worth implementing to cover both `UHttpResponse` and `bool` (and future types) — weigh IL2CPP generic instantiation cost vs. code duplication.

---

## Step 4: Profile and Identify Additional Hot-Path TCS Allocations

**Goal:** Use profiling to identify any remaining `TaskCompletionSource` allocations in hot paths that would benefit from poolable `ValueTask` sources.

Required behavior:

1. Run allocation profiling under load (concurrent HTTP/2 requests with multiplexed streams).
2. Identify `TaskCompletionSource<T>` allocations by count and call site.
3. Categorize each by frequency:
   - **Per-request:** Must optimize (e.g., stream response awaiting).
   - **Per-connection:** Nice to optimize (e.g., settings ack, GOAWAY awaiting).
   - **Per-session:** Not worth optimizing (e.g., initial connection establishment).
4. For any newly identified per-request TCS allocations, apply the poolable `ValueTask` source pattern.

Implementation constraints:

1. **Scope exclusion:** `HappyEyeballsConnector.cs` `TaskCompletionSource` allocations (cancel signals, `Task.WhenAny`) occur once per connection establishment — explicitly out of scope per the phase plan. Defer to Phase 19a if needed.
2. Use `dotnet-counters` or `dotnet-trace` for allocation tracking if Unity Profiler is not suitable.
3. Document all identified TCS allocations with a decision (optimize / defer / skip) and rationale.

---

## Step 5: Optimize Middleware Hop Overhead (if applicable)

**Goal:** After 19.1 migration, verify and optimize the per-hop overhead in the middleware chain.

Required behavior:

1. Measure the overhead of each middleware hop — specifically, the cost of the async state machine generated by the C# compiler for each `async ValueTask<UHttpResponse>` middleware.
2. For middleware with pure synchronous fast paths (no await needed), verify that they return `ValueTask` without allocating an async state machine.
3. Identify any middleware that could be converted from `async ValueTask<T>` methods to non-async methods returning `new ValueTask<T>(result)` on the fast path — this avoids state machine allocation entirely.

Implementation constraints:

1. This step overlaps with 19.1 Step 2 (middleware migration) — the focus here is on the HTTP/2 multiplexed request path specifically, where per-hop allocation matters more at high concurrency.
2. The C# compiler elides the state machine allocation for `async ValueTask<T>` methods that complete synchronously in some compiler versions — verify behavior with the target compiler.
3. Do not break middleware composability for marginal optimization — prioritize correctness.

---

## Verification Criteria

1. IL2CPP AOT smoke test for `IValueTaskSource<T>` passes (or fallback strategy is activated and documented).
2. `Http2Stream` no longer allocates `TaskCompletionSource<UHttpResponse>` per request — uses pooled `ValueTask` source.
3. `Http2Connection` settings ack uses pooled `ValueTask` source.
4. Allocation profiling under HTTP/2 concurrent load shows measurable reduction in `TaskCompletionSource` allocations.
5. All HTTP/2 tests pass — including multiplexed stream tests, GOAWAY handling, RST_STREAM, and flow control.
6. Pooled sources are correctly returned — no pool leaks under sustained load (pool size stays bounded).
7. Cancellation and error paths correctly complete pooled sources — no orphaned sources.
8. `ValueTask` single-consumption guarantee holds — stress test with concurrent multiplexed requests verifies no double-await.
