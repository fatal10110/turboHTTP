# Phase 14.7: Mock Server for Testing

**Depends on:** Phase 7
**Assembly:** `TurboHTTP.Testing`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 1 modified

---

## Step 1: Build In-Process Mock Server Core

**Files:**
- `Runtime/Testing/MockHttpServer.cs` (new)
- `Runtime/Testing/MockRoute.cs` (new)

Required behavior:

1. Register route handlers by method + path matcher.
2. Return deterministic mock responses with status, headers, and body.
3. Support startup/shutdown lifecycle for test setup/teardown.
4. Support call-history capture for assertions.

Implementation constraints:

1. Keep server deterministic and isolated per test fixture.
2. Avoid real external network dependencies for core scenarios.
3. Preserve cancellation behavior in handlers.

---

## Step 2: Add Fluent Test Helper API

**Files:**
- `Runtime/Testing/MockResponseBuilder.cs` (new)
- `Tests/Runtime/Integration/IntegrationTests.cs` (modify)

Required behavior:

1. Provide fluent route registration and response-building helpers.
2. Support one-shot and repeated routes.
3. Support body matchers and header matchers for request assertions.
4. Integrate at least one end-to-end integration test with the mock server.

Implementation constraints:

1. Keep API consistent with existing test helper style from Phase 7.
2. Ensure helpers produce clear assertion failures.
3. Avoid hidden global state between tests.

---

## Verification Criteria

1. Mock server supports deterministic request/response scenarios.
2. Route matching and call-history assertions are reliable.
3. Integration tests can run without live network dependency.
4. Mock server startup/shutdown is stable under parallel test execution.
