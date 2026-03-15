# Phase 22 — Retry Interceptor Review

**Date:** 2026-03-15
**Reviewed by:** unity-infrastructure-architect, unity-network-architect
**Files reviewed:**
- `Runtime/Retry/RetryInterceptor.cs`
- `Runtime/Retry/RetryDetectorHandler.cs`
- `Runtime/Retry/RetryPolicy.cs`
- `Tests/Runtime/Retry/RetryInterceptorTests.cs`

---

## Must Fix

### 1. Mutable `RetryPolicy` static singletons

**Location:** `Runtime/Retry/RetryPolicy.cs:11-12`
**Severity:** High
**Flagged by:** Both agents

`Default` and `NoRetry` are mutable shared instances with public setters. Any code calling `RetryPolicy.Default.MaxRetries = 0` corrupts the singleton globally for all consumers in the same AppDomain.

**Fix:** Make properties `{ get; }` only with constructor initialization, or return defensive copies from `Default`/`NoRetry` properties.

---

### 2. `RetryExhausted` not recorded on terminal retryable exception path

**Location:** `Runtime/Retry/RetryInterceptor.cs:58-64`
**Severity:** High
**Flagged by:** unity-infrastructure-architect

When the terminal attempt delivers a retryable `UHttpException` via `OnResponseError`, `RetryTerminalObserverHandler` sets `DeliveredError = true` and `WasRetryableFailure = true`. But the condition at line 65 is:

```csharp
else if (terminalObserver.WasCommitted && !terminalObserver.DeliveredError)
```

This means neither `RetryExhausted` nor `RetrySucceeded` is recorded when the terminal attempt fails with a retryable exception. The `RetryExhausted` check at line 58 requires `WasRetryableFailure` but that condition is inside `if (terminalObserver.WasRetryableFailure)` which checks only the 5xx response path. When the failure comes via `OnResponseError`, `WasRetryableFailure` is set but `DeliveredError` is also true, so the `RetryExhausted` branch fires correctly — however, the `WasRetryableFailure` flag is overwritten in `OnResponseError` (line 165) after being set in `OnResponseStart` (line 148). If a 5xx `OnResponseStart` is followed by a retryable `OnResponseError`, the flag reflects the error, not the status. This is a reporting gap.

**Fix:** The `RetryExhausted` condition should fire when `WasRetryableFailure` is true regardless of `DeliveredError`.

---

### 3. No validation for `BackoffMultiplier <= 0`

**Location:** `Runtime/Retry/RetryPolicy.cs`
**Severity:** High
**Flagged by:** unity-infrastructure-architect

- `BackoffMultiplier = 0` makes delay permanently zero, causing a retry storm with no backoff.
- `BackoffMultiplier < 0` produces a negative `TimeSpan`, which crashes `Task.Delay` with `ArgumentOutOfRangeException`.

**Fix:** Add validation in `RetryPolicy` (constructor or property setter) to enforce `BackoffMultiplier > 0`. Consider also validating `MaxRetries >= 0`, `InitialDelay >= TimeSpan.Zero`, and `MaxDelay > TimeSpan.Zero`.

---

## Should Fix

### 4. Multiple `OnRequestStart` forwarded to inner handler across retry attempts

**Location:** `Runtime/Retry/RetryDetectorHandler.cs:20-23`
**Severity:** Medium-High
**Flagged by:** unity-network-architect

Each retry iteration forwards `OnRequestStart` to the same inner handler. The `ResponseCollectorHandler` tolerates this (it just overwrites `_request`), but it violates the `IHttpHandler` lifecycle contract which implies one `OnRequestStart` per lifecycle pair (`OnResponseEnd` or `OnResponseError`).

If any handler upstream maintains per-request state initialized in `OnRequestStart`, this will break.

**Fix:** Suppress `OnRequestStart` on retry attempts (attempt > 1), or only forward it on the first attempt. Apply the same to `RetryTerminalObserverHandler`.

---

### 5. No jitter in exponential backoff

**Location:** `Runtime/Retry/RetryInterceptor.cs:120-125`
**Severity:** Medium
**Flagged by:** unity-network-architect

Purely deterministic backoff (`delay * multiplier`, capped at `MaxDelay`) causes thundering herd problems when many clients retry simultaneously against a recovering server. RFC 8981 Section 2 recommends randomized jitter.

**Fix:** Add jitter. Common approaches:
- Full jitter: `delay = random(0, min(cap, base * 2^attempt))`
- Equal jitter: `delay = delay/2 + random(0, delay/2)`

Could be opt-in via a `RetryPolicy.UseJitter` flag (default `true`).

---

### 6. `RetryDetectorHandler` allocated per retry iteration

**Location:** `Runtime/Retry/RetryInterceptor.cs:77`
**Severity:** Medium
**Flagged by:** unity-infrastructure-architect

A new `RetryDetectorHandler` is heap-allocated on every retry iteration inside the `while` loop. For `MaxRetries = 3`, this is 3 allocations of a class with two bool fields and one reference field.

**Fix:** Add an internal `Reset()` method and reuse the same instance across iterations.

---

### 7. `Dictionary<string, object>` allocation per timeline event

**Location:** `Runtime/Retry/RetryInterceptor.cs:46-49, 60-63, 87-89, 96-100`
**Severity:** Low-Medium
**Flagged by:** Both agents

Every `RecordEvent` call allocates a `new Dictionary<string, object>`, and `int`/`double` values are boxed. With `MaxRetries = 3`, a single exhausted request produces up to 7 dictionary allocations. GC pressure concern on mobile platforms.

**Fix:** Consider a struct-based event data approach, or accept as consistent with existing `RecordEvent` pattern across the project.

---

## Low Priority / Document

### 8. `Retry-After` header not respected

**Location:** `Runtime/Retry/RetryDetectorHandler.cs:27-33`
**Flagged by:** unity-network-architect

When a 5xx response is received, the headers are available but discarded. RFC 9110 Section 10.2.3 defines the `Retry-After` header which servers use to communicate when clients should retry. A 503 with `Retry-After: 60` should cause the client to wait at least 60 seconds.

**Recommendation:** Parse `Retry-After` from 5xx response headers when suppressing. Store on the detector and use as a minimum delay override in the retry loop.

---

### 9. `RequestContext.Elapsed` includes retry delays

**Flagged by:** unity-network-architect

The same `RequestContext` is passed to all retry attempts. `context.Elapsed` measures from original request start, so the final `UHttpResponse.Elapsed` reflects total wall-clock time including all retry delays, not just the final attempt's latency.

**Recommendation:** Document this behavior. Optionally provide per-attempt timing via timeline events.

---

### 10. `RetryDetectorHandler` suppresses 5xx lifecycle without terminal callback

**Location:** `Runtime/Retry/RetryDetectorHandler.cs:27-33`
**Flagged by:** unity-network-architect

When a 5xx response is suppressed, the inner handler receives `OnRequestStart` but never receives `OnResponseEnd` or `OnResponseError` for that attempt. This is a contract violation of `IHttpHandler` (every lifecycle must terminate). Currently benign because `ResponseCollectorHandler` is tolerant and `DispatchBridge` uses task continuation.

**Recommendation:** Document as intentional or add synthetic `OnResponseError` before retry.

---

### 11. Missing test cases

**Flagged by:** Both agents

Missing coverage for:
- Cancellation during `Task.Delay` between retries (verify `OperationCanceledException` propagation)
- `BackoffMultiplier = 0` (zero-delay retry storm)
- `BackoffMultiplier < 0` (negative TimeSpan crash)
- `RetrySucceeded` event from non-terminal path (5xx then 2xx on attempt 2 with `MaxRetries = 3`)
- Retryable exception on non-idempotent methods with `OnlyRetryIdempotent = false`
- `NoRetryPolicy_PassesThrough` should assert returned `StatusCode`, not just `RequestCount`
- `MaxRetries = int.MaxValue` creates a practical infinite loop (add documented maximum)

---

### 12. Test infrastructure inconsistency

**Flagged by:** unity-infrastructure-architect

All tests use `Task.Run(async () => { ... }).GetAwaiter().GetResult()` instead of the project's `AssertAsync.Run` utility. The `RetryableErrorAfterResponseStart_IsDeliveredWithoutRetry` test nests `AssertAsync.ThrowsAsync` (which internally calls `Task.Run(...).GetAwaiter().GetResult()`) inside another `Task.Run` block, creating a deadlock risk in environments with a single-threaded `SynchronizationContext`.

**Recommendation:** Standardize to `AssertAsync.Run` pattern.
