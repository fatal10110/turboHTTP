# Phase 19.5: Benchmarks & Regression Validation

**Depends on:** 19.1, 19.2, 19.3, 19.4 (all previous sub-phases)
**Estimated Effort:** 3-4 days

---

## Step 0: Capture Pre-Refactor Baseline (if not already captured)

Required behavior:

1. If not already captured before the Phase 19 refactor began, document baseline allocation counts and throughput numbers from the pre-refactor codebase.
2. Baseline metrics to capture:
   - **Allocation count per request** for HTTP/1.1 single request (warm connection).
   - **Allocation count per request** for HTTP/2 single request (warm connection, multiplexed stream).
   - **Throughput (requests/sec)** under 10, 50, 100, 500 concurrent HTTP/2 streams.
   - **P50/P99 latency** for single request and concurrent request scenarios.
3. Record the baseline in a structured format (table or JSON) for comparison.

Implementation constraints:

1. If the refactor is already complete, use git to check out the pre-refactor commit and capture baselines there.
2. Use consistent test infrastructure (same echo server, same machine, same warm-up period) for before/after comparisons.
3. Minimum 3 runs per benchmark to account for variance — report median.
4. Focus on allocation counts rather than timing for "did the refactor help" analysis — timing improvements are secondary and harder to measure reliably.

---

## Step 1: Implement Allocation Benchmarks

Required behavior:

1. Create allocation benchmarks for the following scenarios:
   - **HTTP/1.1 warm request:** `UHttpClient.GetAsync` to a local echo server with keep-alive connection already established. Measure allocations in the pipeline + transport path only (exclude response body reading).
   - **HTTP/2 warm request:** Same as above but over HTTP/2 with an existing connection. Measure stream creation, header encoding, data frame exchange.
   - **HTTP/2 multiplexed burst:** Send 100 concurrent requests on a single HTTP/2 connection. Measure per-stream allocation overhead.
   - **Middleware chain overhead:** Measure allocations for a request passing through 5-10 `ValueTask`-returning middleware with synchronous fast paths (e.g., header injection, logging, caching hit).
2. Each benchmark must report: total allocations, Gen0 collections, bytes allocated.
3. Benchmarks should isolate the refactor impact by using the same test setup as the baseline.

Implementation constraints:

1. Use BenchmarkDotNet if available, otherwise use `GC.GetAllocatedBytesForCurrentThread()` for manual allocation measurement.
2. In Unity context, use Unity Profiler's allocation tracking or `Profiler.GetMonoUsedSizeLong()` delta.
3. Warm up connections and pools before measuring — the benchmark should measure steady-state behavior, not cold start.
4. Disable GC during measurement runs if using manual tracking (via `GC.TryStartNoGCRegion`).

---

## Step 2: Implement Throughput Benchmarks

Required behavior:

1. Create throughput benchmarks:
   - **HTTP/1.1 sequential:** Send requests sequentially on a single keep-alive connection. Measure requests/sec.
   - **HTTP/1.1 concurrent:** Send requests from N concurrent tasks (N = 10, 50, 100). Measure aggregate requests/sec.
   - **HTTP/2 multiplexed:** Send N concurrent requests on a single HTTP/2 connection (N = 10, 50, 100, 500). Measure aggregate requests/sec and per-stream latency.
2. Each benchmark reports: requests/sec, P50 latency, P99 latency, error rate.
3. Compare against baseline to verify no throughput regression.

Implementation constraints:

1. Use a local echo server that returns a small fixed response (e.g., 100 bytes) to minimize server-side variance.
2. Run benchmarks for at least 10 seconds per scenario to reach steady state.
3. Acceptable result: throughput is equal or better than baseline. Any regression > 5% requires investigation.
4. HTTP/2 multiplexed benchmark must respect server's `MAX_CONCURRENT_STREAMS` setting — do not exceed it.

---

## Step 3: Implement Stress Tests for Cancellation and Race Conditions

Required behavior:

1. **Cancellation storm test:** Send 1000 concurrent requests, cancel 50% of them at random delays (0ms to 100ms after send). Verify:
   - No exceptions leak to the caller beyond `OperationCanceledException`.
   - Connection pool is not corrupted — subsequent requests succeed.
   - No orphaned pooled `ValueTask` sources (pool size stays bounded).
2. **Timeout stress test:** Configure aggressive timeouts (50ms), send requests to a server with variable latency (0-200ms). Verify:
   - Timed-out requests complete with timeout error.
   - Non-timed-out requests complete successfully.
   - Connection pool recovers from timed-out connections.
3. **ValueTask double-await detection test:** Intentionally attempt to await a `ValueTask` twice in a test — verify that the behavior is well-defined (either throws or returns the same result, depending on whether the source has been returned to pool).
4. **HTTP/2 GOAWAY + concurrent requests:** Send GOAWAY from server while 50 concurrent requests are in-flight. Verify all pending requests complete with appropriate errors and pooled sources are returned.

Implementation constraints:

1. Stress tests must be deterministic enough to reproduce failures — use fixed random seeds where randomization is used.
2. Cancellation tests must verify `finally` blocks execute (no resource leaks).
3. Run each stress test for at least 30 seconds or 10,000 iterations.
4. These tests may be slow — mark them with `[Category("Stress")]` or equivalent to exclude from the default test run.

---

## Step 4: Validate API Behavior Regression

Required behavior:

1. Run the full existing test suite — all tests must pass without modification (beyond return type changes done in 19.1).
2. Verify middleware ordering is preserved — test that middleware executes in the correct order (request pipeline → response pipeline).
3. Verify error mapping is preserved — HTTP errors, transport errors, timeout errors, and cancellation errors must produce the same `UHttpError` types as before.
4. Verify retry and redirect middleware behavior is unchanged — retries must reuse the same pipeline path.
5. Verify content negotiation and response deserialization are unaffected.

Implementation constraints:

1. If any test fails, investigate whether it's a genuine regression or a test that was incorrectly testing `Task`-specific behavior (e.g., `Assert.IsType<Task<T>>(...)`).
2. Do not suppress or skip failing tests — fix the underlying issue.
3. Pay special attention to tests that use `Task.WhenAll` or `Task.WhenAny` on request results — these may need `.AsTask()` wrappers.

---

## Step 5: Create CI Gate for Allocation Counts

Required behavior:

1. Create a CI benchmark step that runs the allocation benchmarks (Step 1) and compares against a stored baseline.
2. If allocation count for any scenario exceeds the baseline by more than a configurable threshold (default: 10%), the CI step fails.
3. Store baselines in a version-controlled file (e.g., `Tests/Benchmarks/allocation-baselines.json`).
4. Provide a mechanism to update baselines when intentional allocation changes are made (e.g., a script that runs benchmarks and updates the baseline file).

Implementation constraints:

1. CI allocation tracking must be deterministic — avoid measuring allocations from test infrastructure, logging, or GC bookkeeping.
2. Use per-scenario baselines rather than a single aggregate — a regression in one scenario should not be hidden by improvement in another.
3. The threshold should be configurable via an environment variable for flexibility.
4. Document how to update baselines in the project contributing guide.

---

## Verification Criteria

1. **Allocation reduction:** HTTP/1.1 warm request shows ≥ 1 fewer `Task<T>` allocation per request. HTTP/2 warm request shows ≥ 2 fewer `Task<T>` + `TaskCompletionSource<T>` allocations per request.
2. **No throughput regression:** All throughput benchmarks are within 5% of baseline (or better).
3. **Stress test survival:** Cancellation storm, timeout stress, and GOAWAY stress tests complete without crashes, hangs, or resource leaks.
4. **Full test suite green:** All existing tests pass.
5. **Middleware ordering preserved:** Middleware execution order test passes.
6. **Error mapping preserved:** Error taxonomy tests pass — same error types for same failure conditions.
7. **CI gate operational:** Allocation baseline comparison runs in CI and fails on regression.
8. **ValueTask contract enforced:** No `ValueTask` double-await or storage detected in stress tests or code review.
