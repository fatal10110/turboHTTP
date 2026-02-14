# Phase 7: Testing Infrastructure

**Milestone:** M2 (v0.5 "hardening gate")
**Dependencies:** Phase 6 (Performance & Hardening)
**Estimated Complexity:** High
**Critical:** Yes - Production readiness

## Overview

Implement deterministic test infrastructure, record/replay support, integration categories, and performance/coverage quality gates.

**Detailed breakdown:** `phase7/overview.md`

This document is authoritative for behavior and safety requirements. Any code snippets are illustrative only.

## Goals

1. Provide deterministic transport testing primitives.
2. Add robust record/replay for offline reproducibility.
3. Improve core and integration test coverage.
4. Add benchmark and regression gates for CI.
5. Keep the testing module platform-aware (IL2CPP/AOT constraints).

## Implementation Rules (Must-Have)

1. **AOT-safe serialization only:** testing serialization must not depend on reflection-only paths in IL2CPP builds.
2. **Strict replay semantics:** replay mismatch behavior is configurable and strict by default.
3. **Redaction by policy, not hard-coded keys:** support configurable sensitive headers/params/fields.
4. **Multi-value header fidelity:** recordings preserve header multiplicity.
5. **Deterministic CI lane:** real-internet tests are optional and category-filtered out by default.
6. **Platform test gate:** phase completion requires IL2CPP validation run for testing primitives.

## Tasks

### Task 7.1: Mock Transport

**File:** `Runtime/Testing/MockTransport.cs`

Required behavior:

1. Extend existing `MockTransport` from Phase 4.4 (do not create duplicate implementation).
2. Queue deterministic responses and capture requests in order.
3. `EnqueueJsonResponse<T>` uses project serializer facade (not direct `System.Text.Json`).
4. Delay simulation is cancellation-aware.
5. Empty queue failure is explicit and descriptive.

### Task 7.2: Record/Replay Transport

**File:** `Runtime/Testing/RecordReplayTransport.cs`

Required behavior:

1. Modes: `Record`, `Replay`, `Passthrough`.
2. Request matching strategy:
   - stable request key (method + normalized URL + selected headers + body hash)
   - configurable mismatch policy (`Strict`, `Warn`, `Relaxed`), default `Strict`
   - body hash algorithm: SHA-256 (for large bodies: first 64KB + last 64KB + content length)
3. Recording format supports versioning and timestamps.
4. Headers stored as multi-value structure (`Dictionary<string, List<string>>` or equivalent).
5. Redaction policy is configurable with secure defaults (`Authorization`, `Cookie`, `Set-Cookie`, API key headers, etc.).
6. Transport implements `IDisposable`; record mode flush behavior is explicit.
7. Serialization implementation is AOT-safe.
8. IL2CPP stripping requirements are documented for hashing/serialization types (e.g., `link.xml` where required).
9. Fallback behavior is defined when hashing provider is unavailable on platform.

### Task 7.3: Test Helpers

**File:** `Tests/Runtime/TestHelpers.cs`

Required behavior:

1. Deterministic client factories and request builders.
2. `AssertCompletesWithinAsync` preserves underlying task exceptions.
3. `AssertThrowsAsync<T>` supports optional predicate/details checks.

### Task 7.4: Core Type Tests

**File:** `Tests/Runtime/Core/CoreTypesTests.cs`

Coverage requirements:

1. Happy path + error path for request/response core types.
2. Edge-case tests:
   - invalid/null arguments
   - boundary timeout values
   - header validation and CRLF injection guards
   - immutability/defensive copy behavior

### Task 7.5: Integration Tests

**File:** `Tests/Runtime/Integration/IntegrationTests.cs`

Required behavior:

1. Deterministic integration suite (local/in-process fixtures) enabled in CI.
2. External-network suite (`httpbin` or equivalent) in separate optional category.
3. CI configuration excludes external category by default.
4. Avoid dependencies on modules not yet implemented.

### Task 7.6: Benchmarks and Coverage Gates

**File:** `Tests/Runtime/Performance/BenchmarkTests.cs`

Required behavior:

1. Async benchmarks avoid blocking waits (`task.Wait()` disallowed).
2. Thresholds are realistic and documented per environment.
3. Coverage gate >= 80% with deterministic lane.
4. Regression checks focus on trend/budget, not just single absolute value.
5. Coverage tooling is explicit: Unity Code Coverage package (Editor/Mono lane); IL2CPP validated via functional tests.

## Validation Criteria

### Success Criteria

- [ ] Deterministic unit/integration suite passes in CI.
- [ ] External-network suite is optional and clearly categorized.
- [ ] Record/replay is AOT-safe and schema-versioned.
- [ ] Replay matching is strict by default and configurable.
- [ ] Redaction policy is configurable and enabled by default.
- [ ] Benchmark and coverage gates are reproducible.
- [ ] IL2CPP validation run completed for testing infrastructure paths.

### Recommended Test Commands

```bash
# All runtime tests (deterministic lane)
Unity -runTests -batchmode -projectPath . -testResults ./test-results.xml --where "cat != ExternalNetwork"

# Optional external-network integration tests
Unity -runTests -batchmode -projectPath . -testResults ./test-results-external.xml --where "cat == ExternalNetwork"
```

## Notes

- Prefer local deterministic fixtures over internet-dependent assertions.
- Keep recording artifacts out of source control unless intentionally versioned fixtures.
- Defer any unresolved test-platform limitation with explicit rationale and target phase.
