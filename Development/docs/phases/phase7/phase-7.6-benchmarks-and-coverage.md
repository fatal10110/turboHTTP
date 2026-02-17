# Phase 7.6: Benchmarks, Coverage, and Quality Gates

**Depends on:** Phase 7.4, 7.5
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Add Benchmark Tests

**File:** `Tests/Runtime/Performance/BenchmarkTests.cs`

Coverage:

1. 1000 request throughput baseline with mock transport.
2. Per-request allocation checks for regression detection.
3. Async-only benchmark execution (no blocking `Wait`/`Result`).

---

## Step 2: Define Quality Gates

Required gates:

1. Unit + deterministic integration tests pass.
2. Coverage target at or above 80%.
3. Benchmark thresholds documented with expected environment.
4. No memory leak signals in repeated benchmark loops.
5. External-network tests excluded from required CI lane.
6. Coverage tooling is explicit (Unity Code Coverage package on Editor/Mono lane).

Reference benchmark environment (required):

1. Unity 2021.3 LTS
2. Standalone build baseline (Mono, x64, Release) for primary threshold
3. IL2CPP device/build validation as secondary platform-check gate
4. Coverage percentage gate is measured on Editor/Mono lane; IL2CPP uses functional pass/fail gates.

---

## Step 3: CI Test Command Matrix

Required commands:

1. Full runtime tests.
2. Deterministic-only integration subset.
3. Optional external-network suite.

---

## Verification Criteria

1. Benchmark tests are stable and reproducible.
2. Coverage and pass/fail gates are enforceable in CI.
3. Regression thresholds are documented and actionable.
