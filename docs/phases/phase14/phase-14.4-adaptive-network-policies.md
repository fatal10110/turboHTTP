# Phase 14.4: Adaptive Network Policies

**Depends on:** Phase 14.1
**Assembly:** `TurboHTTP.Transport`, `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 1 modified

---

## Step 1: Add Network Quality Model

**Files:**
- `Runtime/Transport/Adaptive/NetworkQuality.cs` (new)
- `Runtime/Transport/Adaptive/NetworkQualityDetector.cs` (new)

### Technical Spec

Quality enum:

```csharp
public enum NetworkQuality
{
    Excellent,
    Good,
    Fair,
    Poor
}
```

Signal inputs per request attempt:

1. `LatencyMs` from request start to first response byte.
2. `TotalDurationMs` for completed attempts.
3. `WasTimeout`.
4. `WasTransportFailure`.
5. `BytesTransferred`.

Detector algorithm:

1. Maintain bounded ring buffer of last `N` samples (default `N=64`).
2. Compute EWMA for latency and timeout ratio.
3. Compute success ratio over sliding window.
4. Classify quality using threshold table with hysteresis:
   - promote only after `K` consecutive better windows;
   - demote immediately on hard timeout burst threshold.

Suggested baseline thresholds:

| Quality | P50 Latency | Timeout Ratio | Success Ratio |
|---|---|---|---|
| Excellent | `< 120ms` | `< 1%` | `>= 99%` |
| Good | `< 300ms` | `< 3%` | `>= 97%` |
| Fair | `< 900ms` | `< 8%` | `>= 90%` |
| Poor | otherwise | otherwise | otherwise |

Snapshot contract:

```csharp
public readonly struct NetworkQualitySnapshot
{
    public NetworkQuality Quality { get; }
    public double EwmaLatencyMs { get; }
    public double TimeoutRatio { get; }
    public double SuccessRatio { get; }
    public int SampleCount { get; }
}
```

### Implementation Constraints

1. Use monotonic clock source for latency calculations.
2. Detector API must be lock-efficient under high concurrency.
3. Snapshot reads must not allocate.
4. Thresholds/config must be injectable for tests.

---

## Step 2: Implement `AdaptiveMiddleware`

**File:** `Runtime/Core/UHttpClientOptions.cs` (modify)

### Technical Spec

Policy surface:

```csharp
public sealed class AdaptivePolicy
{
    public bool Enable { get; init; }
    public TimeSpan MinTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool AllowConcurrencyAdjustment { get; init; } = true;
    public bool AllowRetryAdjustment { get; init; } = true;
}
```

Behavior mapping by quality:

| Quality | Timeout Multiplier | Concurrency Hint | Retry Backoff Multiplier | Cache Preference |
|---|---|---|---|---|
| Excellent | `0.8x` | baseline + 1 | `0.8x` | normal |
| Good | `1.0x` | baseline | `1.0x` | normal |
| Fair | `1.5x` | baseline - 1 | `1.5x` | prefer cached if available |
| Poor | `2.0x` | baseline - 2 | `2.5x` | strongly prefer cached/safe revalidate |

Middleware integration:

1. Read detector snapshot at request start.
2. Apply adapted defaults only when request does not set explicit overrides.
3. Annotate `RequestContext` with chosen policy parameters.
4. Feed outcome sample back to detector at request completion.

### Implementation Constraints

1. Adaptation must be deterministic for identical snapshots.
2. No mutation of global client settings inside per-request pipeline.
3. Requests with strict user-defined timeout/concurrency must bypass adaptation for those fields.
4. Middleware must tolerate missing detector data (cold start).

---

## Step 3: Add Deterministic Adaptive Policy Tests

**File:** `Tests/Runtime/Transport/AdaptiveMiddlewareTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `ColdStart_UsesBaseline` | no prior samples | baseline policy applied |
| `PoorNetwork_IncreasesTimeout` | synthetic poor snapshot | timeout multiplier applied |
| `ExplicitTimeout_NotOverridden` | request has timeout override | request value preserved |
| `Hysteresis_PreventsThrash` | oscillating samples near threshold | quality remains stable |
| `Recovery_PromotesAfterKWindows` | sustained good samples after poor period | quality improves after hysteresis count |

---

## Verification Criteria

1. Network classification is stable and reproducible under deterministic sample streams.
2. Policy adaptation improves resilience on poor links without overriding explicit request intent.
3. Detector and middleware add negligible overhead on healthy networks.
4. All quality transitions and policy decisions are observable via timeline diagnostics.
