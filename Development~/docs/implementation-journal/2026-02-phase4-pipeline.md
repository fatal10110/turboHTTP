# Phase 4: Pipeline Infrastructure

**Date:** 2026-02-14
**Phase:** 4 (Pipeline Infrastructure)
**Status:** Complete (reviews passed with fixes applied)

## What Was Implemented

ASP.NET Core-style middleware pipeline for request/response interception. The pipeline delegate chain is built once at UHttpClient construction and reused across requests. Includes 3 core middlewares (in Core assembly), 3 module middlewares (in separate assemblies), a mock transport for testing, and comprehensive test coverage.

## Files Created

### Phase 4.1 — Pipeline Executor (1 new, 1 modified)

| File | Description |
|------|-------------|
| `Runtime/Core/Pipeline/HttpPipeline.cs` | Middleware delegate chain executor. Builds chain once at construction (O(n)), reuses across requests (O(1) per-request). Empty middleware list delegates directly to transport. |
| `Runtime/Core/UHttpClient.cs` | **Modified** — Added `_pipeline` field, construct in constructor, replaced `_transport.SendAsync` with `_pipeline.ExecuteAsync` in `SendAsync`. |

### Phase 4.2 — Core Middlewares (3 new, in TurboHTTP.Core assembly)

| File | Description |
|------|-------------|
| `Runtime/Core/Pipeline/Middlewares/LoggingMiddleware.cs` | Request/response logging with configurable log levels (None/Minimal/Standard/Detailed). Action<string> callback, body preview truncation at 500 bytes. |
| `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs` | Adds default headers without overwriting existing ones. Optional `overrideExisting` mode. Defensive header cloning preserves request immutability. |
| `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs` | Per-request timeout enforcement. Returns 408 response with UHttpError (not exception) to enable retry-on-timeout. Distinguishes timeout vs user cancellation via `when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)` — user intent takes precedence. |

### Phase 4.3 — Module Middlewares (7 new, in separate assemblies)

| File | Assembly | Description |
|------|----------|-------------|
| `Runtime/Retry/RetryPolicy.cs` | TurboHTTP.Retry | Configuration: MaxRetries (3), InitialDelay (1s), BackoffMultiplier (2.0), MaxDelay (30s), OnlyRetryIdempotent (true). Static `Default` and `NoRetry` factories. |
| `Runtime/Retry/RetryMiddleware.cs` | TurboHTTP.Retry | Exponential backoff with MaxDelay cap and idempotency guard. Retries on 5xx and retryable UHttpException. Records retry events in RequestContext. |
| `Runtime/Auth/IAuthTokenProvider.cs` | TurboHTTP.Auth | Async token provider interface. Returns null/empty to skip auth. |
| `Runtime/Auth/StaticTokenProvider.cs` | TurboHTTP.Auth | Fixed token provider for API keys. |
| `Runtime/Auth/AuthMiddleware.cs` | TurboHTTP.Auth | Adds Authorization header with configurable scheme (Bearer/Basic/custom). Skips when token is null/empty. CRLF injection validation on both scheme and token. |
| `Runtime/Observability/HttpMetrics.cs` | TurboHTTP.Observability | Thread-safe metrics with Interlocked fields. AverageResponseTimeMs stored as long bits for 32-bit IL2CPP atomicity. ConcurrentDictionary for per-host and per-status counts. |
| `Runtime/Observability/MetricsMiddleware.cs` | TurboHTTP.Observability | Collects request/response metrics. Thread-safe via Interlocked. Reset() for clearing (not thread-safe during active requests). |

### Phase 4.4 — Test Infrastructure (1 new)

| File | Assembly | Description |
|------|----------|-------------|
| `Runtime/Testing/MockTransport.cs` | TurboHTTP.Testing | Three constructor overloads: simple (status/headers/body), full handler (Func with request/context/ct), simplified handler (Func with request only). RequestCount and LastRequest tracking. |

### Phase 4.5 — Tests (8 new)

| File | Description |
|------|-------------|
| `Tests/Runtime/Pipeline/HttpPipelineTests.cs` | Middleware execution order, empty pipeline, short-circuit, exception propagation, null argument validation |
| `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs` | Request/response logging, LogLevel.None passthrough, WARN for non-2xx, ERROR for exceptions |
| `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs` | Header addition, no-override behavior, override mode, original request immutability |
| `Tests/Runtime/Pipeline/TimeoutMiddlewareTests.cs` | Fast request passthrough, slow request returns 408, user cancellation throws OperationCanceledException |
| `Tests/Runtime/Retry/RetryMiddlewareTests.cs` | Success first attempt, retry until success, exhaustion, 4xx no-retry, POST no-retry by default, configurable POST retry, context events, retryable exceptions, NoRetry policy |
| `Tests/Runtime/Auth/AuthMiddlewareTests.cs` | Bearer token, custom scheme, empty token skip, null token skip, null provider throws |
| `Tests/Runtime/Observability/MetricsMiddlewareTests.cs` | Success/failure tracking, per-host counts, per-status counts, bytes sent/received, reset, exception tracking |
| `Tests/Runtime/Integration/PipelineIntegrationTests.cs` | Full pipeline with logging+metrics+headers+auth through UHttpClient, no-middleware backwards compat, Retry+Timeout integration, user cancellation precedence |

## Decisions Made

1. **Pipeline in Core assembly:** Core middlewares (Logging, Timeout, DefaultHeaders) live in `TurboHTTP.Core` since it's `autoReferenced: true`. Users don't need extra assembly references for basic middleware.

2. **Timeout returns 408 response, not exception:** This enables retry-on-timeout when `RetryMiddleware` wraps `TimeoutMiddleware` in the pipeline. User cancellation still propagates as `OperationCanceledException`.

3. **Two pipeline ordering patterns:**
   - Option A (default): Retry → Timeout → ... (per-attempt timeout, retry sees 408)
   - Option B: Timeout → Retry → ... (overall timeout, no retry on timeout)

4. **Module middlewares in separate assemblies:** Retry, Auth, Observability each in their own assembly, referencing only Core. No cross-module dependencies.

5. **MetricsMiddleware uses Interlocked + BitConverter for 32-bit IL2CPP:** Double values stored as long bits for atomic read/write on 32-bit platforms where double writes can tear.

6. **Deferred items:** DecompressionMiddleware, CookieMiddleware, retry jitter, circuit breaker — all planned for later phases.

## Review Findings & Fixes

Two specialist reviews (unity-infrastructure-architect, unity-network-architect) identified the following issues. All HIGH and MEDIUM severity items were fixed in this session.

### Fixed (HIGH + MEDIUM)

| Severity | Component | Issue | Fix |
|----------|-----------|-------|-----|
| HIGH | TimeoutMiddleware | Race condition: user cancellation swallowed as 408 when timeout fires simultaneously | Added `!cancellationToken.IsCancellationRequested` check first in exception filter |
| MEDIUM | DefaultHeadersMiddleware | Multi-value default headers silently dropped (only first value used) | Switched to `Names` + `GetValues()` iteration; added early-exit on empty defaults |
| MEDIUM | RetryPolicy | No maximum backoff cap; delays could grow unboundedly | Added `MaxDelay` property (30s default) with `Math.Min()` clamping |
| MEDIUM | AuthMiddleware | No CRLF validation on scheme/token (defense-in-depth) | Added CR/LF validation in constructor (scheme) and InvokeAsync (token) |
| MEDIUM | MetricsMiddleware | Potential divide-by-zero if Reset() races with active requests | Added `count > 0` guard before dividing |
| MEDIUM | Tests | Missing Retry+Timeout integration test | Added `RetryWrapsTimeout_RetriesOnPerAttemptTimeout` and `UserCancellation_PropagatesEvenWhenTimeoutFires` |

### Deferred (LOW / Phase 10)

| Issue | Target Phase |
|-------|-------------|
| RetryPolicy static properties allocate on each access | Phase 10 |
| Retry jitter for thundering herd prevention | Phase 6 |
| LoggingMiddleware sensitive header redaction | Phase 6/8 |
| String concatenation allocations in LoggingMiddleware/RetryMiddleware | Phase 10 |
| Per-attempt RequestContext in RetryMiddleware | Phase 10 |
| DefaultHeadersMiddleware unnecessary clone when all defaults present | Phase 10 |

## Directory Changes

- Created: `Runtime/Core/Pipeline/`
- Created: `Runtime/Core/Pipeline/Middlewares/`
- Created: `Tests/Runtime/Pipeline/`
- Created: `Tests/Runtime/Auth/`
- Created: `Tests/Runtime/Observability/`
- Created: `Tests/Runtime/Retry/`
- Created: `Tests/Runtime/Integration/`
