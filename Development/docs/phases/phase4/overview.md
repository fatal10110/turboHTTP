# Phase 4 Implementation Plan — Overview

Phase 4 is broken into 5 sub-phases executed sequentially (with 4.2–4.4 parallelizable). Each sub-phase is self-contained with its own files, verification criteria, and review checkpoints.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [4.1](phase-4.1-pipeline-executor.md) | Pipeline Executor + UHttpClient Integration | 1 new + 1 modified | Phase 3 |
| [4.2](phase-4.2-core-middlewares.md) | Core Middlewares (Logging, DefaultHeaders, Timeout) | 3 new | 4.1 |
| [4.3](phase-4.3-module-middlewares.md) | Module Middlewares (Retry, Auth, Metrics) | 7 new | 4.1 |
| [4.4](phase-4.4-test-infrastructure.md) | MockTransport (Testing) | 1 new | 4.1 |
| [4.5](phase-4.5-tests.md) | Tests & Integration | 8 new | 4.2, 4.3, 4.4 |

## Dependency Graph

```
Phase 3 (done)
    └── 4.1 Pipeline Executor + UHttpClient Integration
         ├── 4.2 Core Middlewares (Logging, DefaultHeaders, Timeout)
         ├── 4.3 Module Middlewares (Retry, Auth, Metrics)
         └── 4.4 MockTransport (Testing)
              └── 4.5 Tests & Integration
```

Sub-phases 4.2, 4.3, and 4.4 can be implemented in parallel (no inter-dependencies). Sub-phase 4.5 tests everything.

## Pre-Implementation: Directory Cleanup

Before starting any sub-phase:

1. **Delete** `Runtime/Pipeline/` directory (empty directory with empty `Middlewares/` subdirectory, no `.asmdef`). This was a Phase 1 placeholder that conflicts with the correct location.
2. **Create** `Runtime/Core/Pipeline/` directory
3. **Create** `Runtime/Core/Pipeline/Middlewares/` directory

**Rationale:** Core middlewares (Logging, Timeout, DefaultHeaders) must live in `TurboHTTP.Core` assembly since it's `autoReferenced: true`. A separate `Pipeline` assembly would force users to add an extra reference for basic functionality.

## Existing Foundation (Phase 3)

### Core Types (all in `Runtime/Core/`, namespace `TurboHTTP.Core`):

| Type | Key APIs for Phase 4 |
|------|---------------------|
| `IHttpMiddleware` | Already defined: `InvokeAsync(request, context, next, ct)` |
| `HttpPipelineDelegate` | Already defined: `delegate Task<UHttpResponse>(request, context, ct)` |
| `UHttpClient` | `SendAsync()` calls `_transport.SendAsync()` directly — Phase 4 inserts pipeline |
| `UHttpClientOptions` | `Middlewares` list exists (stub), `Clone()` deep-copies list |
| `UHttpRequest` | Immutable, `WithHeaders()`, `Timeout` property, `Method`, `Uri`, `Headers`, `Body` |
| `UHttpResponse` | `StatusCode`, `IsSuccessStatusCode`, `ElapsedTime`, `Headers`, `Body`, `Error` |
| `RequestContext` | `RecordEvent()`, `SetState()`, `GetState<T>()`, `UpdateRequest()`, `Elapsed`, `Stop()` |
| `UHttpError` | `IsRetryable()`, `UHttpErrorType` enum |
| `UHttpException` | `HttpError` property |
| `HttpHeaders` | `Clone()`, `Set()`, `Contains()`, implements `IEnumerable<KeyValuePair<string, string>>` |
| `HttpMethod` | `IsIdempotent()`, `HasBody()` extensions |
| `IHttpTransport` | `SendAsync(request, context, ct)`, extends `IDisposable` |

### Assembly Structure:

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Core` | none | true | Core middlewares go here |
| `TurboHTTP.Retry` | Core | false | RetryMiddleware goes here |
| `TurboHTTP.Auth` | Core | false | AuthMiddleware goes here |
| `TurboHTTP.Observability` | Core | false | MetricsMiddleware goes here |
| `TurboHTTP.Testing` | Core | false | MockTransport goes here |
| `TurboHTTP.Tests.Runtime` | All modules | false | All tests |

## Cross-Cutting Design Decisions

1. **Pipeline caching:** Build delegate chain once per `UHttpClient` instance (in constructor), reuse across all requests. `_options.Middlewares` is already deep-copied by `UHttpClientOptions.Clone()`, so the list is immutable after construction.

2. **Middleware ordering (user responsibility):** Order in `UHttpClientOptions.Middlewares` list determines execution order. First middleware in list = outermost (sees raw request/final response). **Two recommended configurations:**

   **Option A — Retry on timeout (per-attempt timeout, recommended default):**
   - LoggingMiddleware (first — captures everything including retries)
   - MetricsMiddleware (captures all attempts)
   - RetryMiddleware (outermost of the two — sees 408 from Timeout, can retry)
   - TimeoutMiddleware (applies timeout per individual attempt)
   - AuthMiddleware
   - DefaultHeadersMiddleware (last — closest to transport)

   **Option B — Overall timeout (no retry on timeout):**
   - LoggingMiddleware
   - MetricsMiddleware
   - TimeoutMiddleware (outermost — enforces total time budget for all attempts)
   - RetryMiddleware (retries within the time budget, but timeouts are NOT retried)
   - AuthMiddleware
   - DefaultHeadersMiddleware

   **Why this matters:** First middleware in list wraps all subsequent ones. In Option B, `TimeoutMiddleware` wraps `RetryMiddleware` — when timeout fires, Timeout catches the `OperationCanceledException` and returns 408 directly to `UHttpClient`, so Retry never sees it. In Option A, `RetryMiddleware` wraps `TimeoutMiddleware` — Retry sees the 408 response from Timeout and can retry the request.

3. **Error model in middleware:**
   - `TimeoutMiddleware` returns a `UHttpResponse` with `408 RequestTimeout` status + `UHttpError` (does NOT throw). This allows `RetryMiddleware` to handle timeouts as retryable responses **when Retry is positioned before Timeout in the list** (Option A).
   - `RetryMiddleware` catches `UHttpException` when `IsRetryable()` returns true. Also checks `response.Error?.IsRetryable()` for non-exception retryable responses (e.g., 408 timeout, 5xx). Returns last failed response when retries are exhausted.
   - Other middlewares propagate exceptions unchanged.

4. **Request immutability:** Middlewares that modify requests (DefaultHeaders, Auth) create new `UHttpRequest` instances via `WithHeaders()` and call `context.UpdateRequest()` to track the transformation.

5. **HttpHeaders enumeration:** `HttpHeaders` implements `IEnumerable<KeyValuePair<string, string>>` (returns first value per header name). The spec's `foreach (var header in request.Headers)` with `header.Key` / `header.Value` works directly. For detailed multi-value logging, use `header.Names` + `GetValues()`.

6. **Module file splitting:** Complex types are split into separate files for clarity:
   - `RetryPolicy.cs` + `RetryMiddleware.cs` (in Retry)
   - `IAuthTokenProvider.cs` + `StaticTokenProvider.cs` + `AuthMiddleware.cs` (in Auth)
   - `HttpMetrics.cs` + `MetricsMiddleware.cs` (in Observability)

7. **Scope exclusions (deferred):** The original Phase 4 spec mentions `DecompressionMiddleware` and `CookieMiddleware` in the goals but provides no implementation tasks or code. These are deferred to a later phase.

8. **`OperationCanceledException` handling:** `UHttpClient.SendAsync` already catches `OperationCanceledException` separately from `UHttpException`. The `TimeoutMiddleware` must ensure that user-initiated cancellation (not timeout) propagates as `OperationCanceledException`, not as a 408 response. This is achieved by checking `timeoutCts.IsCancellationRequested` vs `cancellationToken.IsCancellationRequested`.

## All Files (20 new, 1 modified, 1 deleted)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| — | Delete | `Runtime/Pipeline/` (empty dir) | — |
| 1 | Create | `Runtime/Core/Pipeline/HttpPipeline.cs` | Core |
| 2 | Modify | `Runtime/Core/UHttpClient.cs` | Core |
| 3 | Create | `Runtime/Core/Pipeline/Middlewares/LoggingMiddleware.cs` | Core |
| 4 | Create | `Runtime/Core/Pipeline/Middlewares/DefaultHeadersMiddleware.cs` | Core |
| 5 | Create | `Runtime/Core/Pipeline/Middlewares/TimeoutMiddleware.cs` | Core |
| 6 | Create | `Runtime/Retry/RetryPolicy.cs` | Retry |
| 7 | Create | `Runtime/Retry/RetryMiddleware.cs` | Retry |
| 8 | Create | `Runtime/Auth/IAuthTokenProvider.cs` | Auth |
| 9 | Create | `Runtime/Auth/StaticTokenProvider.cs` | Auth |
| 10 | Create | `Runtime/Auth/AuthMiddleware.cs` | Auth |
| 11 | Create | `Runtime/Observability/HttpMetrics.cs` | Observability |
| 12 | Create | `Runtime/Observability/MetricsMiddleware.cs` | Observability |
| 13 | Create | `Runtime/Testing/MockTransport.cs` | Testing |
| 14 | Create | `Tests/Runtime/Pipeline/HttpPipelineTests.cs` | Tests |
| 15 | Create | `Tests/Runtime/Pipeline/LoggingMiddlewareTests.cs` | Tests |
| 16 | Create | `Tests/Runtime/Pipeline/TimeoutMiddlewareTests.cs` | Tests |
| 17 | Create | `Tests/Runtime/Pipeline/DefaultHeadersMiddlewareTests.cs` | Tests |
| 18 | Create | `Tests/Runtime/Retry/RetryMiddlewareTests.cs` | Tests |
| 19 | Create | `Tests/Runtime/Auth/AuthMiddlewareTests.cs` | Tests |
| 20 | Create | `Tests/Runtime/Observability/MetricsMiddlewareTests.cs` | Tests |
| 21 | Create | `Tests/Runtime/Integration/PipelineIntegrationTests.cs` | Tests |

## Post-Implementation

1. Run both specialist agent reviews (unity-infrastructure-architect, unity-network-architect)
2. Create `Development/docs/implementation-journal/2026-02-phase4-pipeline.md`
3. Update `CLAUDE.md` Development Status section
