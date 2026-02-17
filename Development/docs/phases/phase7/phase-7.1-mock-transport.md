# Phase 7.1: Mock Transport

**Depends on:** Phase 6 (Performance & Hardening)
**Assembly:** `TurboHTTP.Testing`
**Files:** 1 modified (extends existing Phase 4.4 implementation)

---

## Step 1: Extend Existing `MockTransport`

**File:** `Runtime/Testing/MockTransport.cs`

Required behavior:

1. Queue deterministic mock responses.
2. Capture incoming requests for assertions.
3. Support configurable delay per response.
4. Return `UHttpResponse` objects matching configured fixtures.
5. Implement `IDisposable` contract expected by `IHttpTransport`.

---

## Step 2: Add Convenience Methods

Required behavior:

1. `EnqueueJsonResponse(...)` helper for JSON fixtures via project serializer facade (`TurboHTTP.JSON.JsonSerializer`).
2. `EnqueueError(...)` helper for error-path tests.
3. Clear captured request history between test cases.

Implementation constraints:

1. No network usage.
2. Clear exception when queue is empty.
3. Safe for repeated use in same test run.
4. Thread-safety assumptions are explicit (single-threaded test usage or synchronized access).
5. If concurrent test use is supported, response/capture collections must be thread-safe.
6. Default policy: thread-safe implementation is required (supports parallel test runners).

---

## Verification Criteria

1. Requests are captured in send order.
2. Delayed responses respect cancellation token behavior.
3. JSON and error helper paths produce expected responses.
