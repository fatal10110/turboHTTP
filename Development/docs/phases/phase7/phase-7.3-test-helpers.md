# Phase 7.3: Test Helpers

**Depends on:** Phase 7.1
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Add Shared Test Factory Helpers

**File:** `Tests/Runtime/TestHelpers.cs`

Required behavior:

1. `CreateMockClient()` returns `(UHttpClient, MockTransport)` tuple.
2. `CreateRequest(...)` builds default requests quickly.
3. Include async assertion helpers (`AssertCompletesWithinAsync`, `AssertThrowsAsync`).
4. Timeout helper must preserve original task exception details when task completes before timeout.
5. Throw helper supports optional predicate for message/detail validation.

Implementation constraints:

1. Helpers must be deterministic.
2. No static mutable state that bleeds across tests.

---

## Verification Criteria

1. Helpers reduce duplication across core/integration/performance tests.
2. Timeout and exception assertions behave consistently in async tests.
