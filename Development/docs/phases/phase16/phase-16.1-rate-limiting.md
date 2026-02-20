# Phase 16.1: Rate Limiting (Token Bucket + Middleware)

**Depends on:** Phase 4 (Pipeline Infrastructure)
**Assembly:** `TurboHTTP.RateLimit`, `TurboHTTP.Tests.Runtime`
**Files:** 5 new, 1 modified

---

## Step 1: Implement Token Bucket Algorithm

**Files:**
- `Runtime/RateLimit/TokenBucket.cs` (new)
- `Runtime/RateLimit/RateLimitPolicy.cs` (new)

Required behavior:

1. Implement classic token bucket with configurable capacity and refill rate.
2. Use `Stopwatch`-based monotonic timing for deterministic refill calculations (not `DateTime`).
3. Use `Interlocked` operations for thread-safe, lock-free token acquisition (IL2CPP 32-bit safe).
4. Support two acquisition modes: `TryAcquire` (fail-fast, returns `bool`) and `WaitAsync` (async delay until token available, with `CancellationToken`).
5. Refill tokens based on elapsed time since last refill, capped at bucket capacity.
6. Expose current token count and next refill time for diagnostics.
7. Avoid `Thread.Sleep` or blocking waits — async delay path must use `Task.Delay` with cancellation.
8. Handle edge cases: zero-capacity bucket (always reject), negative elapsed time (clock skew protection).

Implementation constraints:

1. No lock-based synchronization — use `Interlocked.CompareExchange` spin loops for atomic token updates.
2. Refill calculation must avoid floating-point drift by using integer tick arithmetic.
3. `WaitAsync` must respect cancellation and not leak waiters on timeout.
4. Must be allocation-free on the fast path (token available, no wait needed).

---

## Step 2: Define Rate Limit Policy Configuration

**File:** `Runtime/RateLimit/RateLimitPolicy.cs` (modify from Step 1)

Required behavior:

1. Define `RateLimitPolicy` with configurable properties:
   - `MaxRequests` (int) — bucket capacity / max requests per window.
   - `TimeWindow` (TimeSpan) — refill period.
   - `PerHost` (bool) — whether to maintain separate buckets per host.
   - `BehaviorWhenLimited` (enum: `Reject`, `Wait`) — fail-fast vs. async wait.
   - `MaxWaitTime` (TimeSpan) — maximum time to wait when `BehaviorWhenLimited` is `Wait`.
2. Provide sensible defaults: 60 requests/minute, per-host enabled, reject on limit.
3. Support global policy overrides (single bucket for all hosts).
4. Validate policy on construction — reject invalid configurations (zero window, negative capacity).

Implementation constraints:

1. Policy must be immutable after construction (set via constructor or init-only properties).
2. Keep configuration surface minimal — avoid over-engineering per-endpoint policies in this phase.
3. Policy validation errors must be descriptive and thrown at configuration time, not at request time.

---

## Step 3: Implement Rate Limit Middleware

**Files:**
- `Runtime/RateLimit/RateLimitMiddleware.cs` (new)
- `Runtime/RateLimit/RateLimitExceededException.cs` (new)

Required behavior:

1. Implement `IHttpMiddleware` that checks token availability before forwarding request to pipeline.
2. Maintain per-host `TokenBucket` instances when `PerHost` is enabled (keyed by `Uri.Host`).
3. Maintain single global `TokenBucket` when `PerHost` is disabled.
4. On `Reject` mode: throw `RateLimitExceededException` with retry-after hint when bucket is empty.
5. On `Wait` mode: await token availability up to `MaxWaitTime`, then throw if still unavailable.
6. Record timeline events in `RequestContext`: `RateLimitAcquired`, `RateLimitWaited`, `RateLimitRejected`.
7. Handle `Retry-After` header from 429 responses: parse value (seconds or HTTP-date) and temporarily reduce bucket refill rate or pause the bucket for the specified duration.
8. Implement `IDisposable` to clean up per-host bucket dictionary and any pending waiters.
9. Support exempting specific requests via `RequestContext` metadata key (`RateLimitExempt`).

Implementation constraints:

1. Per-host bucket dictionary must be thread-safe (`ConcurrentDictionary` or lock-based).
2. Bucket creation for new hosts must be lazy and bounded (cap max tracked hosts to prevent unbounded memory growth).
3. `Retry-After` parsing must handle both delta-seconds (integer) and HTTP-date (RFC 7231) formats gracefully.
4. Middleware must not modify request or response — only gate/delay execution.
5. `RateLimitExceededException` must extend `UHttpException` with `UHttpErrorType` integration.
6. Stale host buckets should be eligible for cleanup (LRU eviction or time-based expiry).

---

## Step 4: Add Assembly Definition and Builder Extensions

**Files:**
- `Runtime/RateLimit/TurboHTTP.RateLimit.asmdef` (modify — placeholder exists)
- `Runtime/RateLimit/RateLimitBuilderExtensions.cs` (new)

Required behavior:

1. Configure assembly definition: references `TurboHTTP.Core`, `autoReferenced: false`, `noEngineReferences: true`.
2. Add builder extension method `WithRateLimit(RateLimitPolicy)` on `UHttpClientOptions` or equivalent configuration surface.
3. Add convenience extension `WithRateLimit(int maxRequests, TimeSpan window)` for common configurations.
4. Ensure middleware is added to pipeline in correct order (rate limiting should execute early, before retry/auth).

Implementation constraints:

1. Follow existing builder extension pattern from `TurboHTTP.Auth.AuthBuilderExtensions`.
2. Extension methods must be in `TurboHTTP.RateLimit` namespace so users opt in via `using` directive.
3. Do not add any references to assemblies other than `TurboHTTP.Core`.

---

## Step 5: Add Rate Limiting Tests

**File:** `Tests/Runtime/RateLimit/RateLimitTests.cs` (new)

Required behavior:

1. Validate token bucket correctly limits requests at configured rate.
2. Validate token refill behavior over simulated time progression.
3. Validate per-host isolation (requests to different hosts use separate buckets).
4. Validate global mode (single bucket for all hosts).
5. Validate `Reject` mode throws `RateLimitExceededException` when bucket is empty.
6. Validate `Wait` mode delays execution and succeeds after refill.
7. Validate `Wait` mode respects `MaxWaitTime` and throws on timeout.
8. Validate cancellation propagation through `WaitAsync`.
9. Validate `Retry-After` header parsing and bucket pause behavior on 429 responses.
10. Validate concurrent access safety under multi-thread flood using `MockTransport`.
11. Validate timeline event recording for rate limit actions.
12. Validate stale bucket cleanup does not corrupt active buckets.
13. Validate request exemption via metadata key.

---

## Verification Criteria

1. Token bucket correctly enforces configured request rate under sustained load.
2. Per-host isolation prevents one domain's traffic from affecting another's quota.
3. `Wait` mode successfully delays and completes requests without deadlock.
4. `Reject` mode provides actionable error with retry-after timing hint.
5. 429 `Retry-After` response dynamically adjusts bucket behavior.
6. Thread safety holds under concurrent multi-thread access on IL2CPP targets.
7. Zero allocations on fast path (token available, no wait).
8. Middleware integrates cleanly with existing pipeline (retry, auth, logging all compose correctly).
