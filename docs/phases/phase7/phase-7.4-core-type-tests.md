# Phase 7.4: Core Type Unit Tests

**Depends on:** Phase 7.1, 7.3
**Assembly:** `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Add Core Request/Response Tests

**File:** `Tests/Runtime/Core/CoreTypesTests.cs`

Coverage:

1. `UHttpRequest` constructor and immutable mutation methods.
2. `UHttpResponse` success/error status semantics.
3. `GetBodyAsString()` behavior for UTF-8 payloads.
4. `EnsureSuccessStatusCode()` error path.

---

## Step 2: Add Edge-Case Assertions

Coverage:

1. Null/body-less response scenarios.
2. Header manipulation behavior (including injection/validation checks).
3. Exception types/messages for invalid states.
4. Boundary values (timeouts, empty headers, empty body).
5. Defensive-copy and immutability guarantees.

---

## Verification Criteria

1. Core unit tests pass reliably and independently.
2. Assertions cover both happy path and failure path behavior.
