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
5. Expose configurable capture size policy (`MaxCaptureSize` default 5 MB) and binary payload preview behavior.

Implementation constraints:

1. Middleware failures in capture path must not break request pipeline.
2. Capture failures must surface diagnostics with explicit throttling policy (log first error immediately, then suppress repeats for a fixed cooldown window).
3. Shared history must be thread-safe and allocation-conscious using a bounded ring buffer (fixed capacity) or equivalent O(1) bounded structure.
4. History cap must be configurable with deterministic eviction strategy.
5. History read APIs used by UI must avoid per-frame allocations (for example snapshot into caller-provided buffer).
6. Capture policy should keep text bodies (up to configured limit) and avoid fully buffering large binary payloads (preview or metadata only).
7. Header masking/redaction hooks must be opt-in and configurable (default disabled).
8. Listener callback failures must be isolated so one bad subscriber cannot break capture flow.

---

## Verification Criteria

1. Successful and failed requests are both captured with correct metadata.
2. History remains bounded under high request throughput with deterministic oldest-entry eviction.
3. Clear-history operation empties capture store and updates listeners.
4. Capture path does not materially change request latency in stress tests.
5. Capture exceptions are visible in diagnostics and do not spam logs under repeated failures.
6. Large binary responses do not produce full in-memory body copies by default.
7. UI history retrieval path does not allocate a new list each repaint cycle.
