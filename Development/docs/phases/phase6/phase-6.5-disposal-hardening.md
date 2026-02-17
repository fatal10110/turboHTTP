# Phase 6.5: UHttpClient Disposal Hardening

**Depends on:** Phase 6.2, 6.3, 6.4
**Assembly:** `TurboHTTP.Core`
**Files:** 1 modified

---

## Step 1: Implement Full Dispose Pattern

**File:** `Runtime/Core/UHttpClient.cs`

Required behavior:

1. Implement `IDisposable` with idempotent disposal.
2. Dispose transport if it implements `IDisposable`.
3. Dispose middlewares that implement `IDisposable`.
4. Call `GC.SuppressFinalize(this)` from `Dispose()`.
5. Dispose queue/limiter resources owned by the client when configured.
6. Dispose middleware in reverse registration order (LIFO), then dispose transport last.

---

## Step 2: Add Disposal Guardrails

Required behavior:

1. `ThrowIfDisposed()` prevents sends after disposal.
2. Calls after dispose fail fast with `ObjectDisposedException`.
3. Existing success/error request behavior remains unchanged before disposal.
4. Guards are applied across all public request entry points, not only `SendAsync`.

Implementation constraints:

1. No double-dispose side effects.
2. No transport/middleware leaks under error paths.
3. Middleware collection is iterated from immutable snapshot/clone owned by the client.
4. Disposal continues best-effort across multiple disposable components (aggregate/log exceptions rather than leak).

---

## Verification Criteria

1. Repeated dispose calls are safe.
2. Post-dispose API usage throws consistent exceptions.
3. Transport and disposable middlewares are released exactly once.
4. Queue/limiter owned resources are released exactly once.
