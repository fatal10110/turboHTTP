# Phase 9.4: Platform Test Suite and Matrix

**Depends on:** Phase 9.2, Phase 9.3
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Create Platform Validation Tests

**File:** `Tests/Runtime/Platform/PlatformTests.cs`

Required behavior:

1. Add deterministic tests for platform detection and configuration helpers.
2. Add IL2CPP compatibility check tests as release-gate tests.
3. Add transport smoke tests for HTTPS/TLS behavior.
4. Separate external-network tests from deterministic tests via category tags.
5. Add ALPN protocol-negotiation tests that verify both HTTP/2 success and HTTP/1.1 fallback behavior.
6. Add concrete IL2CPP JSON roundtrip tests (nested DTOs, collections, nullable fields) in test assemblies that reference JSON modules.

Implementation constraints:

1. External endpoint tests must not run in the default deterministic CI lane (`[Category("ExternalNetwork")]`).
2. Use clear test naming for platform/backend matrix reporting.
3. Ensure test failures include platform and backend in assertion messages.
4. Avoid test flakiness from long-lived global state.
5. Keep deterministic transport tests in `[Category("Deterministic")]` and run them in default CI.

---

## Step 2: Produce Platform Matrix Evidence

Required behavior:

1. Capture pass/fail results for each target platform/backend row.
2. Include at least one validated run per supported release target.
3. Record HTTP/2 ALPN outcome and fallback outcome where applicable.
4. Capture ALPN-negative-case evidence: when h2 is unavailable, client downgrades cleanly to HTTP/1.1 with no request failure.
5. Capture TLS failure-path evidence: certificate validation failure and handshake timeout produce deterministic surfaced errors.

---

## Verification Criteria

1. Deterministic platform tests pass in Editor CI.
2. IL2CPP player test lanes pass on targeted platforms.
3. External-network tests are isolated and opt-in.
4. Platform matrix is complete and attached to release readiness checks.
5. ALPN success, ALPN fallback, and TLS failure paths are explicitly validated and documented.
