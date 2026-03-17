# Phase 22: Retry Review Fixes

**Date:** 2026-03-15  
**Phase:** 22 retry follow-up (`phase-22-retry-interceptor-review.md`)  
**Status:** Retry review findings addressed in code and targeted regressions; Unity compile/device validation still pending

## What Was Implemented

This pass closes the actionable retry-specific findings from `Development/docs/phases/phase22/phase-22-retry-interceptor-review.md`.

The main fixes were:

1. Hardened `RetryPolicy` so shared static policies are no longer mutable global singletons:
   - `RetryPolicy.Default` and `RetryPolicy.NoRetry` now return defensive copies
   - `RetryInterceptor` snapshots the supplied policy at construction time
   - validation now rejects `MaxRetries < 0`, `InitialDelay < 0`, `BackoffMultiplier <= 0` / non-finite values, and `MaxDelay <= 0`
2. Moved retry delay calculation into `RetryPolicy.ComputeDelay(...)` and added equal-jitter backoff by default to reduce synchronized retry storms.
3. Added `Retry-After` support for suppressed retryable 5xx responses: `RetryDetectorHandler` now parses the header and the retry loop treats it as a minimum delay override.
4. Fixed retry telemetry and terminal-attempt handling:
   - `RetryExhausted` now records whenever the terminal attempt ends in any retryable failure path
   - terminal observer retryability is now accumulated instead of overwritten across `OnResponseStart(...)` and `OnResponseError(...)`
5. Removed per-attempt `RetryDetectorHandler` allocation by making the detector resettable and reusing a single instance for the full retry loop.
6. Stopped forwarding duplicate `OnRequestStart(...)` callbacks to the downstream handler on retry attempts, while preserving the original callback for the first dispatch attempt.
7. Reworked the retry tests to the project-standard `AssertAsync.Run(...)` pattern and added focused regressions for:
   - defensive static policy copies
   - interceptor policy snapshotting
   - invalid retry policy configuration
   - retryable terminal exceptions recording `RetryExhausted`
   - cancellation during retry backoff
   - `Retry-After` delay override
   - non-idempotent retryable exception retries when explicitly enabled
   - one-time downstream `OnRequestStart(...)` forwarding across retries

## Files Modified

| File | Change |
|------|--------|
| `Runtime/Retry/RetryPolicy.cs` | Added validation, defensive static policy copies, interceptor snapshot support, and jittered delay computation. |
| `Runtime/Retry/RetryInterceptor.cs` | Snapshots policy input, reuses the detector, suppresses duplicate request-start forwarding, fixes terminal retry telemetry, and consumes computed delays. |
| `Runtime/Retry/RetryDetectorHandler.cs` | Added resettable state, `Retry-After` parsing, and first-attempt-only request-start forwarding. |
| `Tests/Runtime/Retry/RetryInterceptorTests.cs` | Migrated to `AssertAsync.Run(...)` and added targeted retry regressions. |
| `Development/docs/implementation-journal/2026-03-phase22-retry-review-fixes.md` | This implementation record. |

## Assembly Boundary Check

Reviewed the affected assembly boundaries before landing the changes:

- `Runtime/Retry/TurboHTTP.Retry.asmdef`
- `Tests/Runtime/TurboHTTP.Tests.Runtime.asmdef`

No asmdef dependency changes were required. `TurboHTTP.Retry` still depends only on `TurboHTTP.Core`, and the new retry behavior stayed inside the existing optional-module boundary.

## Specialist Review Re-Run

Both required review rubrics were re-run explicitly against the retry diff:

- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

### Findings Closed

- Mutable shared `RetryPolicy.Default` / `RetryPolicy.NoRetry`
- Missing `BackoffMultiplier <= 0` validation
- Missing jitter in retry backoff
- Per-iteration `RetryDetectorHandler` allocation
- Duplicate downstream `OnRequestStart(...)` forwarding across retry attempts
- Terminal retryable-failure telemetry gaps on the last attempt
- Missing retry coverage for cancellation, non-idempotent exception retries, success/exhausted events, and invalid policy values
- Retry test harness inconsistency (`Task.Run(...).GetAwaiter().GetResult()`)
- `Retry-After` header handling on retryable 5xx responses

## Decisions / Trade-Offs

1. `RetryPolicy` remains mutable for compatibility with the existing public object-initializer usage, but the dangerous shared-singleton behavior was removed by returning defensive copies and snapshotting policies inside `RetryInterceptor`.
2. Retry jitter uses equal jitter instead of full jitter to keep delays bounded between 50% and 100% of the capped exponential delay.
3. `Retry-After` is treated as a minimum delay override and is allowed to exceed the configured `MaxDelay`, matching the server-directed backoff expectation from the review.

## Deferred / Remaining

- Retry timeline events still allocate `Dictionary<string, object>` payloads and box numeric values. This remains consistent with the current `RequestContext.RecordEvent(...)` API and was not expanded into a broader timeline-allocation refactor here.
- `RequestContext.Elapsed` still represents total wall-clock time across all attempts, including retry delays.
- Suppressed retryable 5xx attempts still do not emit synthetic terminal callbacks to the downstream handler; the runtime `transport.self_drains_response_body` guard remains the enforced safety contract.
- Unity Editor compile/test execution was not run from this workspace.
- IL2CPP/mobile validation for the updated retry behavior remains pending.

## Validation

- `git diff --check` passes after the retry changes.
- Confirmed `Tests/Runtime/Retry/RetryInterceptorTests.cs` no longer uses direct outer `Task.Run(...).GetAwaiter().GetResult()` wrappers.
- Not completed: Unity batchmode/test-runner execution or generated-solution compilation. This workspace still does not expose a runnable `.sln` / `.csproj` or Unity batch runner entrypoint for end-to-end compilation here.
