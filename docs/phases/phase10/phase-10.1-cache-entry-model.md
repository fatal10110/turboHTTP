# Phase 10.1: Cache Entry Model

**Depends on:** Phase 9
**Assembly:** `TurboHTTP.Cache`
**Files:** 1 new

---

## Step 1: Create `CacheEntry`

**File:** `Runtime/Cache/CacheEntry.cs`

Required behavior:

1. Represent one cached response snapshot: key, body, headers, status, and timestamps.
2. Track cache-control metadata (`ExpiresAt`, `ETag`, `LastModified`).
3. Track request/variant metadata (`ResponseUrl`, `VaryHeaders`, `VaryKey`) used for cache-key matching and 304 updates.
4. Expose `IsExpired(...)` and `CanRevalidate()` helpers.
5. Use UTC timestamps consistently.

Implementation constraints:

1. Entry snapshots must not reference mutable request/response objects directly.
2. Body ownership must be explicit (clone or immutable ownership contract).
3. Methods must be deterministic and side-effect free.
4. Header model must preserve enough data for conditional revalidation.
5. Store enough metadata to merge 304 response headers into cached entries without losing variant information.

---

## Verification Criteria

1. Expiration check returns expected value for expired and non-expired entries.
2. Revalidation check returns `true` only when validator fields are present.
3. Entry serialization/deserialization roundtrip preserves key and variant metadata.
4. Entry can represent empty-body responses without errors.
