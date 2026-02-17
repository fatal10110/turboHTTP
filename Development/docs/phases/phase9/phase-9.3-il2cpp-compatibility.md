# Phase 9.3: IL2CPP Compatibility Validation

**Depends on:** Phase 9.1
**Assembly:** `TurboHTTP.Core`
**Files:** 1 new

---

## Step 1: Build `IL2CPPCompatibility` Checks

**File:** `Runtime/Core/IL2CPPCompatibility.cs`

Required behavior:

1. Run Core-safe compatibility checks for reflection usage, async/await flow, and cancellation behavior.
2. Aggregate check results into a single pass/fail result.
3. Emit actionable diagnostics for each failed check.
4. Keep check methods side-effect free outside logging.
5. Keep compatibility checks deterministic and independent from external network access.

Implementation constraints:

1. Use concrete DTO types in compatibility checks (no anonymous/dynamic types).
2. `Runtime/Core/IL2CPPCompatibility.cs` must not reference `TurboHTTP.JSON` to preserve Core assembly isolation.
3. JSON-specific IL2CPP checks are required, but they run in `Tests/Runtime/Platform/PlatformTests.cs` (or JSON module tests), not in Core.
4. Avoid blocking waits that can deadlock main-thread execution paths.
5. Keep checks lightweight so they can run in smoke tests.

---

## Verification Criteria

1. Core compatibility validation returns `true` in validated IL2CPP environments.
2. Failed checks identify the failing subsystem (reflection/async/cancellation).
3. JSON IL2CPP compatibility is validated in platform tests without introducing Core->JSON dependency.
4. Validation is deterministic across repeated invocations in the same runtime.
5. Checks execute within test-friendly latency bounds.
