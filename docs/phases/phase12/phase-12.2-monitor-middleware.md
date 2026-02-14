# Phase 12.2: Monitor Collector Middleware

**Depends on:** Phase 12.1
**Assembly:** `TurboHTTP.Observability`
**Files:** 1 new

---

## Step 1: Implement `MonitorMiddleware`

**File:** `Runtime/Observability/MonitorMiddleware.cs`

Required behavior:

1. Capture request/response/error snapshots around `next(...)` execution.
2. Publish events to listeners for real-time editor updates.
3. Maintain bounded in-memory history for monitor browsing.
4. Expose clear-history API for tooling controls.

Implementation constraints:

1. Middleware failures in capture path must not break request pipeline.
2. Capture failures must surface diagnostics with explicit throttling policy (log first error immediately, then suppress repeats for a fixed cooldown window).
3. Shared history must be thread-safe and allocation-conscious using a bounded ring buffer (fixed capacity) or equivalent O(1) bounded structure.
4. History cap must be configurable with deterministic eviction strategy.
5. Redaction/truncation policy hooks must be available before persisting payloads.
6. Listener callback failures must be isolated so one bad subscriber cannot break capture flow.

---

## Verification Criteria

1. Successful and failed requests are both captured with correct metadata.
2. History remains bounded under high request throughput with deterministic oldest-entry eviction.
3. Clear-history operation empties capture store and updates listeners.
4. Capture path does not materially change request latency in stress tests.
5. Capture exceptions are visible in diagnostics and do not spam logs under repeated failures.
