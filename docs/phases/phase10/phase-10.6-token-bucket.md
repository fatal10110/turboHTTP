# Phase 10.6: Token Bucket Limiter

**Depends on:** Phase 10.5
**Assembly:** `TurboHTTP.RateLimit`
**Files:** 1 new

---

## Step 1: Implement `TokenBucket`

**File:** `Runtime/RateLimit/TokenBucket.cs`

Required behavior:

1. Support non-blocking `TryAcquireAsync` semantics.
2. Support wait-based `AcquireAsync(ct)` semantics when configured.
3. Refill tokens based on elapsed time and configured interval/rate.
4. Expose safe diagnostics for currently available tokens.
5. Support configurable burst behavior (initial token count defaults to full bucket).

Implementation constraints:

1. Use one synchronization strategy for token state (no mixed lock race).
2. Use a monotonic clock source (`Stopwatch.GetTimestamp` / `Stopwatch.Frequency`), never wall-clock time.
3. Do not busy-wait; use bounded async delays when waiting.
4. Respect cancellation while waiting for token availability.
5. Keep refill math deterministic and monotonic using scaled integer token accounting (for example millitokens in `long`) to avoid 32-bit IL2CPP atomic-double pitfalls.
6. Refill arithmetic must use timestamp deltas based on `Stopwatch.Frequency` and guard overflow via checked bounds.

---

## Verification Criteria

1. Requests under configured limit are admitted.
2. Requests over limit are denied or delayed according to policy.
3. Tokens refill correctly across elapsed intervals.
4. Parallel callers do not produce negative token counts or oversubscription.
5. Cancellation during wait exits promptly without token leakage.
6. Sustained throughput under load stays within expected tolerance of configured rate (for example <=1 percent drift over 60 seconds).
7. Clock adjustments in system time do not alter limiter behavior.
8. 32-bit IL2CPP test lane confirms token accounting has no torn reads/writes under concurrency.
