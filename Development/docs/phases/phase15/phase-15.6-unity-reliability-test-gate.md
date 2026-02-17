# Phase 15.6: Unity Reliability Test Gate

**Depends on:** Phase 15.1-15.5, Phase 15.7
**Assembly:** `TurboHTTP.Tests.Runtime` (+ CI configuration)
**Files:** 3 new, 1 modified

---

## Step 1: Create Reliability and Stress Test Suites

**Files:**
- `Tests/Runtime/Unity/UnityReliabilitySuiteTests.cs` (new)
- `Tests/Runtime/Unity/UnityStressSuiteTests.cs` (new)

Required behavior:

1. Add `[UnityTest]` suites covering dispatcher, texture pipeline, audio pipeline, and coroutine lifecycle invariants.
2. Add dispatcher flood tests from multiple worker threads.
3. Add large texture burst tests with frame-budget assertions.
4. Add audio temp-file churn tests with cleanup assertions.
5. Add deterministic cancellation/reload transition scenarios.

Implementation constraints:

1. Stress tests must remain deterministic enough for CI (bounded virtual time where possible).
2. Tests must isolate temporary resources and clean up even on failure.
3. Avoid flaky timing assertions based purely on wall-clock variance.
4. Keep stress suite split from baseline functional suite so targeted reruns are practical.

---

## Step 2: Add Performance Budgets and Regression Guards

**File:** `Tests/Runtime/Unity/UnityPerformanceBudgetTests.cs` (new)

Required behavior:

1. Define regression guard metrics:
   - max dispatch queue depth under synthetic load,
   - max frame-time impact for texture/audio stress scenario,
   - max leaked temp files after cleanup cycle.
2. Fail tests when budget limits are exceeded.
3. Emit machine-readable metrics to support CI trend tracking.

Implementation constraints:

1. Budget values must be configurable per target profile (Editor vs mobile).
2. Metric capture should be lightweight and test-friendly.
3. Guard tests must fail with actionable diagnostics and threshold values.

---

## Step 3: Enforce Platform Certification Matrix in CI

**File:** CI runtime test config update (modify)

Required behavior:

1. Add mandatory matrix runs for supported release targets:
   - Editor PlayMode (Mono),
   - Standalone players (Windows/macOS/Linux) for supported backend,
   - iOS IL2CPP,
   - Android IL2CPP ARM64,
   - WebGL fallback smoke (when WebGL support is enabled for release).
2. Publish compatibility artifact: `TestResults/unity-platform-matrix.json`.
3. Fail CI on any red matrix cell for release-blocking scenarios.
4. Use tiered execution: PR pipelines run smoke matrix subset, while release-candidate pipelines run full mandatory matrix.

Implementation constraints:

1. Matrix job naming must be stable for release dashboard parsing.
2. Artifact schema must remain backward compatible once introduced.
3. Unsupported platform cells must be explicitly marked `policy-disabled`, not silently skipped.
4. CI capacity plan (runner pool, parallelism limits, retry strategy) must be documented so full-matrix gates remain reliable.

---

## Verification Criteria

1. Reliability suites run consistently in CI without flaky pass/fail churn.
2. Performance budgets catch regressions with actionable metrics.
3. Matrix artifact is generated and complete for every release-candidate run.
4. Phase 15 release gate blocks on matrix failures and reliability regressions.
