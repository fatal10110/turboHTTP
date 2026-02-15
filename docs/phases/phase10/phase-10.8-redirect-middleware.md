# Phase 10.8: Redirect Middleware

**Depends on:** Phase 9
**Assembly:** `TurboHTTP.Middleware`, `TurboHTTP.Tests.Runtime`
**Files:** 1 new

---

## Step 1: Implement `RedirectMiddleware`

**File:** `Runtime/Middleware/RedirectMiddleware.cs`

Required behavior:

1. Enforce `UHttpClientOptions.FollowRedirects`; bypass redirect handling when disabled.
2. Handle redirect status codes `301`, `302`, `303`, `307`, and `308`.
3. Enforce `UHttpClientOptions.MaxRedirects` (default `10`) with deterministic failure when exceeded.
4. Resolve relative `Location` headers against the current request URI.
5. Apply method/body rules:
   - convert `POST` to `GET` for `301`, `302`, `303`;
   - preserve method and body for `307`, `308`.
6. Strip `Authorization` and other origin-bound sensitive headers on cross-origin redirects.
7. Record redirect chain metadata in `RequestContext` for diagnostics.
8. Detect redirect loops and fail fast with actionable error details.

Implementation constraints:

1. Use iterative redirect processing (no unbounded recursion).
2. Preserve request immutability by cloning per-hop requests instead of mutating originals.
3. Keep cancellation responsive across chained redirects.
4. Preserve final response semantics when no redirect applies.
5. Keep per-hop logs low-noise by default; rely on timeline events for diagnostics.
6. Document replayability requirement: redirect-following requires request bodies to be replayable; current `UHttpRequest` byte-array body model satisfies this for Phase 10.

---

## Step 2: Add Focused Unit Tests

**File:** `Tests/Runtime/Middleware/RedirectMiddlewareTests.cs`

Required behavior:

1. Redirect chain follows expected target and returns terminal response.
2. `MaxRedirects` cap fails deterministically when exceeded.
3. Method rewrite logic matches RFC behavior for `301`/`302`/`303` vs `307`/`308`.
4. Cross-origin redirects remove `Authorization`.
5. Relative `Location` values are resolved correctly.

---

## Verification Criteria

1. Redirect behavior is deterministic for all supported status codes.
2. Loop and over-limit scenarios fail with clear, actionable errors.
3. Request metadata (headers/method/body) is transformed only where required.
4. Timeline events capture redirect progression without noisy logging.
