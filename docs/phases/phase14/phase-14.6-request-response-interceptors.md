# Phase 14.6: Request/Response Interceptors

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 1 new, 2 modified

---

## Step 1: Define Interceptor Contracts

**Files:**
- `Runtime/Core/IHttpInterceptor.cs` (new)
- `Runtime/Core/UHttpClient.cs` (modify)

Required behavior:

1. Support request pre-send interception and response post-receive interception.
2. Allow read-only inspection and explicit mutation-capable paths.
3. Provide deterministic execution ordering when multiple interceptors are registered.
4. Surface interceptor failures with clear attribution.

Implementation constraints:

1. Preserve current middleware pipeline semantics.
2. Keep interceptor invocation allocation-light for hot paths.
3. Prevent silent swallowing of exceptions unless policy explicitly opts in.

---

## Step 2: Integrate Interceptors with Request Pipeline

**File:** `Runtime/Core/UHttpClientOptions.cs` (modify)

Required behavior:

1. Invoke request interceptors before middleware execution.
2. Invoke response interceptors after middleware/transport completion.
3. Allow optional short-circuit response generation for test and simulation scenarios.
4. Emit timeline diagnostics for interceptor entry/exit and failures.

Implementation constraints:

1. Respect cancellation tokens at every interceptor boundary.
2. Ensure interceptors cannot mutate unrelated concurrent requests.
3. Keep backward compatibility for existing middleware-based customizations.

---

## Verification Criteria

1. Interceptors execute in deterministic order with expected request/response visibility.
2. Interceptor exceptions propagate according to configured policy.
3. Existing middleware behavior remains unchanged when no interceptors are registered.
4. Interceptor short-circuit paths are covered by tests.
