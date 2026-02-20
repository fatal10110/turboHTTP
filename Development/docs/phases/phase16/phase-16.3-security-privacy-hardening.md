# Phase 16.3: Security & Privacy Hardening

**Depends on:** Phase 6 (Performance & Hardening), Phase 10 (Advanced Middleware)
**Assembly:** `TurboHTTP.Security`, `TurboHTTP.Tests.Runtime`
**Files:** 4 new, 3 modified

---

## Step 1: Centralize and Extend Sensitive Data Redaction

**Files:**
- `Runtime/Security/RedactionPolicy.cs` (new)
- `Runtime/Observability/LoggingMiddleware.cs` (modify)

Required behavior:

1. Define `RedactionPolicy` as a centralized, reusable configuration for sensitive data handling across all modules.
2. Support configurable header redaction sets:
   - Default set: `Authorization`, `Cookie`, `Set-Cookie`, `Proxy-Authorization`, `WWW-Authenticate`, `X-Api-Key`.
   - User-extensible: allow adding custom header names to redaction set.
   - User-reducible: allow removing headers from default set for debugging scenarios.
3. Support body redaction hooks: configurable delegate for redacting sensitive fields in request/response bodies (e.g., password fields in JSON).
4. Support URL query parameter redaction (e.g., `?api_key=***`).
5. Define redaction output format: `[REDACTED]` for headers, configurable placeholder for body fields.
6. Refactor `LoggingMiddleware` to consume `RedactionPolicy` instead of inline `HashSet<string>`.
7. Preserve backward compatibility: existing `LoggingMiddleware` constructor overloads must continue to work with deprecation path.

Implementation constraints:

1. `RedactionPolicy` must be immutable after construction.
2. Header name matching must remain case-insensitive.
3. Body redaction delegate must be optional (null = no body redaction) and must not throw — wrap in try/catch with fallback to full redaction.
4. Policy must be serialization-safe (no delegates in serialized form) for potential future config-file support.
5. Redaction must never log the original sensitive value, even in error paths.

---

## Step 2: Add Cache Partitioning Policy

**Files:**
- `Runtime/Security/CachePartitionPolicy.cs` (new)
- `Runtime/Cache/CacheMiddleware.cs` (modify)

Required behavior:

1. Define `CachePartitionPolicy` to prevent cross-user or cross-origin cache poisoning.
2. Support partitioning strategies:
   - `ByOrigin` — cache keys include request origin (scheme + host + port).
   - `ByAuthToken` — cache keys include hash of `Authorization` header value.
   - `ByCustomKey` — user-provided key extraction delegate.
   - `None` — default (existing behavior, shared cache).
3. Integrate partition key into `CacheMiddleware` cache key computation.
4. Ensure partition boundaries are enforced: a request with partition key A must never receive a cached response from partition key B.
5. Support cache isolation per partition (independent eviction, independent size limits if configured).
6. Record cache partition key in `RequestContext` timeline for diagnostics.

Implementation constraints:

1. Partition key computation must not include raw sensitive values — use SHA-256 hash for `ByAuthToken`.
2. Hash computation must use `System.Security.Cryptography.SHA256` (available in .NET Standard 2.1).
3. `ByCustomKey` delegate must be deterministic for equivalent inputs.
4. Partition policy must be configurable at `CacheMiddleware` construction time.
5. Default behavior (`None`) must preserve existing cache semantics with zero overhead.
6. Partition key must be included in cache key before any normalization to prevent cross-partition collisions.

---

## Step 3: Add TLS Pinning Hooks

**Files:**
- `Runtime/Security/TlsPinningPolicy.cs` (new)
- `Runtime/Transport/RawSocketTransport.cs` (modify)

Required behavior:

1. Define `TlsPinningPolicy` supporting certificate pinning validation:
   - Pin by Subject Public Key Info (SPKI) hash (SHA-256 of DER-encoded SubjectPublicKeyInfo).
   - Pin by certificate thumbprint (SHA-256 of full DER-encoded certificate).
   - Support pin sets per host (different pins for different domains).
   - Support backup pins (multiple valid pins per host for rotation).
2. Integrate with `SslStream` server certificate validation callback in `TlsStreamWrapper`.
3. On pin validation failure: reject connection with `UHttpException(UHttpErrorType.TlsPinningFailure)` (new error type).
4. On pin validation success: allow connection to proceed normally.
5. Support reporting mode (`ReportOnly`): log pin failures but allow connection (for gradual rollout).
6. Support pin expiry: optional expiry date per pin set, after which pinning is bypassed with warning.

Implementation constraints:

1. Pin comparison must be constant-time to prevent timing side-channels (`CryptographicOperations.FixedTimeEquals` where available, manual constant-time comparison otherwise).
2. SPKI extraction must work with `X509Certificate2` from `SslStream` callback — use `certificate.PublicKey` and DER encoding.
3. Pin hashes must be provided as base64-encoded SHA-256 strings (matching HTTP Public Key Pinning format).
4. Pinning must not interfere with existing `SslStream` certificate validation chain.
5. `ReportOnly` mode must use structured logging/diagnostics, not `Debug.Log`.
6. Pinning policy must be optional — null/empty policy means no pinning (existing behavior).
7. Add `UHttpErrorType.TlsPinningFailure` to error type enum.

---

## Step 4: Add Security & Privacy Tests

**File:** `Tests/Runtime/Security/SecurityHardeningTests.cs` (new)

Required behavior:

1. Validate default redaction set covers all standard sensitive headers.
2. Validate custom header addition to redaction set.
3. Validate URL query parameter redaction.
4. Validate body redaction delegate invocation and error handling.
5. Validate `LoggingMiddleware` backward compatibility with existing constructor overloads.
6. Validate cache partitioning by origin (different origins get isolated cache entries).
7. Validate cache partitioning by auth token (different tokens get isolated cache entries).
8. Validate cross-partition isolation (partition A response not served to partition B request).
9. Validate TLS pin validation with matching pin (connection allowed).
10. Validate TLS pin validation with non-matching pin (connection rejected with correct error type).
11. Validate `ReportOnly` mode logs but allows connection.
12. Validate backup pin rotation (primary fails, backup succeeds).
13. Validate pin expiry bypass behavior after expiry date.
14. Validate constant-time pin comparison does not short-circuit.

---

## Verification Criteria

1. Sensitive data is never logged in plain text through any TurboHTTP logging path.
2. Cache partitioning prevents cross-user response leakage under concurrent mixed-auth workloads.
3. TLS pinning correctly rejects connections with invalid certificates.
4. `ReportOnly` mode enables safe gradual rollout of pinning policies.
5. All security defaults are safe-by-default — users must explicitly opt out, not opt in.
6. Existing middleware and transport behavior is preserved when security features are not configured.
7. No performance regression on hot paths when security features use default (no-op) configuration.
