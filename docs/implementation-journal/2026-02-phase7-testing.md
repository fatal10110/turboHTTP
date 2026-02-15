# Phase 7: Testing Infrastructure — 2026-02-15

## What Was Implemented

Phase 7 (M2 hardening gate) was implemented across testing transport primitives, record/replay infrastructure, deterministic integration coverage, and benchmark/quality-gate tests.

## Files Created

### Runtime/Testing/
- **RecordReplayTransport.cs** — New record/replay wrapper transport with:
  - Modes: `Record`, `Replay`, `Passthrough`
  - Schema-versioned recording payload with timestamp metadata
  - Stable request key matching (`method + normalized URL + selected headers + body hash`)
  - Mismatch policies (`Strict` default, `Warn`, `Relaxed`)
  - Per-request-key concurrent replay queues (no global replay lock)
  - Redaction policy object with secure defaults (headers, query keys, optional JSON fields)
  - SHA-256 request-body hashing with large-body strategy (first 64KB + last 64KB + content length for >1MB)
  - Explicit fail-fast diagnostics when SHA-256 provider is unavailable
  - `SaveRecordings()` API and auto-flush on dispose (configurable)
- **link.xml** — IL2CPP stripping preservation guidance for SHA-256 hashing types used by record/replay.

### Tests/Runtime/
- **TestHelpers.cs** — Shared deterministic test helpers:
  - `(UHttpClient, MockTransport) CreateMockClient(...)`
  - `CreateRequest(...)`
  - `AssertCompletesWithinAsync(...)` (Task and Task<T>, preserves task exceptions)
  - `AssertThrowsAsync<T>(...)` with optional predicate/details checks

### Tests/Runtime/Core/
- **CoreTypesTests.cs** — New core-type unit coverage for request/response happy paths and edge cases:
  - Immutable request mutation semantics and defensive header copy behavior
  - Response status semantics, UTF-8 body decoding, and ensure-success error paths
  - Null/invalid argument checks and builder-level CRLF header injection guards
  - Boundary timeout value handling checks

### Tests/Runtime/Integration/
- **IntegrationTests.cs** — New integration suite with deterministic + optional external split:
  - Deterministic mock-based GET/JSON/404 flows
  - MockTransport queue/capture/delay/helper behavior checks
  - Record-then-replay equivalence tests
  - Strict replay mismatch behavior
  - Redaction verification in saved artifacts
  - Parallel replay determinism checks
  - Optional `[Category("ExternalNetwork")]` test (`httpbin`)
  - Explicit deferred HTTP/2 integration note targeting Phase 9 platform validation

### Tests/Runtime/Performance/
- **BenchmarkTests.cs** — New benchmark/quality-gate suite:
  - 1000-request throughput baseline with mock transport
  - Allocation-per-request regression guardrail
  - Repeated-loop leak trend guard
  - CI command matrix notes for deterministic lane, external lane, and coverage lane

## Files Modified

### Runtime/Testing/
- **MockTransport.cs** — Extended Phase 4.4 implementation with:
  - Thread-safe response queue (`EnqueueResponse`, `EnqueueJsonResponse`, `EnqueueError`)
  - Request capture history (`CapturedRequests`, `ClearCapturedRequests`)
  - Cancellation-aware delayed responses
  - Explicit empty-queue failure when no fallback handler exists
  - Backward compatibility for existing constructor-based fallback behaviors
  - Reflection-based use of `TurboHTTP.JSON.JsonSerializer` to avoid new compile-time cross-module dependencies

## Decisions Made

1. **Preserve optional module boundaries:** `TurboHTTP.Testing` was kept free of compile-time references to `TurboHTTP.JSON`. JSON facade calls are done via reflection to avoid adding cross-module assembly references.
2. **AOT-safe recording payload shape:** recording DTOs are represented as dictionary/list structures for serializer compatibility with default `LiteJsonSerializer` while keeping explicit typed models in transport code.
3. **Deterministic replay concurrency:** replay uses per-request-key `ConcurrentQueue` structures with consumable nodes so strict and relaxed keys can coexist without global locking.
4. **Strict-by-default replay semantics:** mismatch behavior defaults to `Strict`; `Warn` and `Relaxed` are opt-in.
5. **HTTP/2 integration defer note:** dedicated HTTP/2 platform integration validation is deferred to Phase 9 (IL2CPP/platform compatibility gate), documented directly in integration tests.

## Validation Notes

- Unity Test Runner execution was not run in this environment (no Unity CLI project test run executed here).
- Deterministic/external lane split and coverage-lane command matrix were added to tests/documentation for CI integration.
