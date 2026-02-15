# Phase 10.9: Cookie Middleware

**Depends on:** Phase 10.8
**Assembly:** `TurboHTTP.Middleware`, `TurboHTTP.Tests.Runtime`
**Files:** 2 new

---

## Step 1: Implement Cookie Jar

**File:** `Runtime/Middleware/CookieJar.cs`

Required behavior:

1. Parse and store cookies from `Set-Cookie` headers using RFC 6265 semantics.
2. Track cookie attributes: `Domain`, `Path`, `Expires/Max-Age`, `Secure`, `HttpOnly`, `SameSite`.
3. Select matching cookies for outbound requests by domain/path/scheme/expiry rules.
4. Support eviction limits:
   - at least `50` cookies per domain;
   - at least `3000` cookies total.
5. Support in-memory default storage with optional persistent backing.

Implementation constraints:

1. Domain/path matching must be case-correct and RFC-compliant.
2. Expiry evaluation must use UTC timestamps and deterministic ordering.
3. Replace existing cookie by (`Name`, `Domain`, `Path`) tuple.
4. Keep operations thread-safe for concurrent HTTP/2 stream usage.
5. Avoid unbounded memory growth from hostile or high-cardinality cookie sets.
6. Use one explicit synchronization strategy for jar state (for example `ReaderWriterLockSlim` or equivalent) and avoid mixed locking patterns.

---

## Step 2: Implement `CookieMiddleware`

**File:** `Runtime/Middleware/CookieMiddleware.cs`

Required behavior:

1. Inject outbound `Cookie` header values from the cookie jar before transport execution.
2. Parse inbound `Set-Cookie` headers and persist valid cookies after response.
3. Honor `Secure` cookies only over HTTPS requests.
4. Respect `SameSite` constraints for request context where applicable.
5. Preserve transparent behavior when no cookies match.

Implementation constraints:

1. Do not merge cookies from unrelated domains into one request.
2. Preserve ordering and duplicate-value behavior compatible with existing header model.
3. Keep middleware cancellation-safe and free of blocking operations on hot path.
4. Diagnostics should expose counts and updates without logging full cookie values.

---

## Step 3: Add Focused Unit Tests

**File:** `Tests/Runtime/Middleware/CookieMiddlewareTests.cs`

Required behavior:

1. Matching cookies are attached to outbound requests.
2. `Set-Cookie` parsing populates jar entries correctly.
3. Expired cookies are not sent.
4. Domain/path mismatch cookies are excluded.
5. Per-domain and total cookie limits are enforced.

---

## Verification Criteria

1. Cookie injection and persistence are deterministic across repeated requests.
2. RFC 6265 matching behavior is validated with targeted edge cases.
3. Concurrent access does not corrupt jar state.
4. Cookie storage remains bounded under stress.
