# Phase 14.6: Request/Response Interceptors

**Depends on:** Phase 12
**Assembly:** `TurboHTTP.Core`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new, 2 modified

---

## Step 1: Define Interceptor Contracts

**Files:**
- `Runtime/Core/IHttpInterceptor.cs` (new)
- `Runtime/Core/UHttpClient.cs` (modify)

### Technical Spec

Interceptor contract:

```csharp
public interface IHttpInterceptor
{
    ValueTask<InterceptorRequestResult> OnRequestAsync(
        UHttpRequest request,
        RequestContext context,
        CancellationToken cancellationToken);

    ValueTask<InterceptorResponseResult> OnResponseAsync(
        UHttpRequest request,
        UHttpResponse response,
        RequestContext context,
        CancellationToken cancellationToken);
}
```

Result model semantics:

1. `Continue` with optional modified request/response snapshot.
2. `ShortCircuit` with synthetic response.
3. `Fail` with explicit exception.

Ordering rules:

1. Request interceptors execute in registration order.
2. Response interceptors execute in reverse registration order.
3. Short-circuit response skips transport and middleware execution but still runs response interceptor phase for already-entered interceptors.

Mutation rules:

1. Default request/response objects treated as immutable snapshots.
2. Mutation requires explicit clone/replace return object.
3. Interceptors may add context metadata through scoped key-value bag only.

### Implementation Constraints

1. No global static interceptor registry.
2. Zero-allocation fast path when no interceptors are registered.
3. Support synchronous interceptors via completed `ValueTask`.
4. No silent exception swallowing unless failure policy explicitly set to continue.

---

## Step 2: Integrate Interceptors Into Pipeline

**Files:**
- `Runtime/Core/UHttpClientOptions.cs` (modify)
- `Runtime/Core/UHttpClient.cs` (modify)

### Technical Spec

Pipeline execution order:

1. Request interceptor chain.
2. Middleware pipeline.
3. Transport.
4. Response interceptor chain.

Exception policy:

```csharp
public enum InterceptorFailurePolicy
{
    Propagate,
    ConvertToResponse,
    IgnoreAndContinue
}
```

Cancellation behavior:

1. Cancellation token checked before each interceptor invocation.
2. Cancelled interceptor call aborts chain and returns cancellation.
3. Response interceptors are skipped if cancellation occurred before response creation.

Diagnostics:

1. Emit `interceptor.request.enter/exit` with interceptor id.
2. Emit `interceptor.response.enter/exit` with interceptor id.
3. Emit `interceptor.shortcircuit` and `interceptor.failure`.

### Implementation Constraints

1. Interceptor execution must not reorder middleware behavior.
2. Concurrency safety: no cross-request mutable interceptor state unless interceptor explicitly manages it.
3. Preserve backward compatibility for existing middleware-only extensions.

---

## Step 3: Add Deterministic Interceptor Tests

**File:** `Tests/Runtime/Core/InterceptorPipelineTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `Ordering_RequestForward_ResponseReverse` | 3 interceptors | exact order asserted |
| `ShortCircuit_SkipsTransport` | request interceptor returns synthetic response | transport not invoked |
| `FailurePolicy_Propagate` | interceptor throws | exception propagated |
| `FailurePolicy_ConvertToResponse` | interceptor throws | mapped error response returned |
| `RequestClone_Isolation` | interceptor mutates clone | original request unchanged |
| `Cancellation_StopsChain` | token cancelled before second interceptor | chain aborts deterministically |
| `NoInterceptors_FastPath` | empty registry | no additional allocations/hops |

---

## Verification Criteria

1. Interceptor lifecycle is deterministic, observable, and fully covered by tests.
2. Short-circuit and failure policies behave exactly as configured.
3. Existing middleware/transport behavior is unchanged when no interceptors are registered.
4. Hot-path overhead for empty interceptor list remains negligible.
