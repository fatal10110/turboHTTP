# Phase 14.4: Adaptive Network Policies

**Depends on:** Phase 14.1
**Assembly:** `TurboHTTP.Transport`, `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 1 modified

---

## Step 1: Add Network Quality Model

**Files:**
- `Runtime/Transport/Adaptive/NetworkQuality.cs` (new)
- `Runtime/Transport/Adaptive/NetworkQualityDetector.cs` (new)

Required behavior:

1. Classify network quality (`Excellent`, `Good`, `Fair`, `Poor`) from latency/loss/timeouts.
2. Maintain rolling-window metrics with bounded memory.
3. Expose deterministic snapshot API for middleware and diagnostics.

Implementation constraints:

1. Use monotonic timers for RTT/jitter calculations.
2. Avoid oscillation with hysteresis thresholds.
3. Keep detector side-effect free outside explicit sample ingestion.

---

## Step 2: Implement `AdaptiveMiddleware`

**File:** `Runtime/Core/UHttpClientOptions.cs` (modify)

Required behavior:

1. Adjust request timeout budgets by current network quality.
2. Reduce concurrency and retry aggressiveness on degraded networks.
3. Prefer cached paths when policy and response semantics allow.
4. Emit policy decisions into `RequestContext` timeline events.

Implementation constraints:

1. All adaptations are opt-in behind explicit policy config.
2. Never violate request-level explicit overrides.
3. Policy decisions must be deterministic for identical detector snapshots.

---

## Verification Criteria

1. Poor-network conditions trigger expected policy adjustments.
2. Healthy networks retain current default behavior and throughput.
3. Detector noise does not cause high-frequency policy thrashing.
4. Adaptive behavior is testable with deterministic synthetic samples.
