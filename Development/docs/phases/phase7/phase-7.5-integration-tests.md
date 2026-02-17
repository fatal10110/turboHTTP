# Phase 7.5: Integration Test Suite

**Depends on:** Phase 7.1, 7.2, 7.3
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Add Integration Test Class

**File:** `Tests/Runtime/Integration/IntegrationTests.cs`

Coverage:

1. Deterministic/local GET request flow.
2. JSON POST roundtrip behavior.
3. HTTP error status handling (e.g., 404).

---

## Step 2: Split Deterministic vs External Tests

Required behavior:

1. Keep deterministic integration tests mock/local where possible.
2. Tag real-network tests (`httpbin`, etc.) as optional category (e.g., `ExternalNetwork`).
3. Ensure CI can exclude external-network tests by filter.
4. Include HTTP/2-focused integration coverage (ALPN negotiation, multiplexing, or explicit deferred note with target phase).
5. HTTP/2 integration coverage is required for phase sign-off unless explicitly deferred with rationale + target phase.

Implementation constraints:

1. Avoid coupling to not-yet-implemented modules.
2. Avoid flaky assertions on timing/network jitter.
3. Deterministic suite remains required; external-network suite remains non-blocking.

---

## Verification Criteria

1. Deterministic integration subset passes in CI.
2. External integration tests pass when internet is available.
3. Test categories/filters are documented.
