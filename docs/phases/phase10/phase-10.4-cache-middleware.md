# Phase 10.4: Cache Middleware and Revalidation

**Depends on:** Phase 10.3
**Assembly:** `TurboHTTP.Cache`
**Files:** 1 new

---

## Step 1: Implement Cache Flow

**File:** `Runtime/Cache/CacheMiddleware.cs`

Required behavior:

1. Cache only safe methods by default (GET; optional HEAD support if enabled).
2. Generate stable cache keys from method + normalized URL + resolved `Vary` dimensions.
3. Treat `Vary: *` as uncacheable.
4. Return cached response on cache hit when revalidation is not required.
5. Cache successful upstream responses according to policy.
6. Default invalidation policy: non-safe methods (`POST`, `PUT`, `PATCH`, `DELETE`) invalidate cached `GET`/`HEAD` entries for the same normalized URI.

Implementation constraints:

1. Normalize URL for keys using: lowercased host, default-port normalization, stripped fragment, and sorted query parameters.
2. Resolve `Vary` from response headers and include listed request-header values in the variant key for both store and lookup.
3. Path normalization rules must be explicit: resolve dot segments, preserve trailing slash significance, and normalize percent-encoding for unreserved characters only.
4. `Vary` key construction must be deterministic: case-insensitive header-name matching, sorted vary header names, explicit empty value token for missing request headers.
5. Conservative privacy defaults for variants: `Vary: Cookie` and `Vary: Authorization` are non-cacheable unless explicitly opted in.
6. Respect `Cache-Control` directives with explicit precedence: `max-age` overrides `Expires`.
7. Directive handling must be explicit:
   - `no-store`: never write to cache
   - `no-cache`: may store, but always revalidate before serving
   - `private`: cache only in private per-client cache scope
8. If no explicit freshness info exists, default behavior is conservative (`DoNotCacheWithoutFreshness = true`), with optional heuristic mode behind policy.
9. Avoid caching responses tied to sensitive auth context unless explicitly enabled.
10. Preserve response semantics when serving from cache (status, headers, body).
11. Keep hot-path logging disabled or minimal by default.

---

## Step 2: Implement Conditional Revalidation

Required behavior:

1. Send `If-None-Match` and/or `If-Modified-Since` from cache validators.
2. On `304 Not Modified`, merge allowed metadata headers from 304 response into cached entry and serve cached body.
3. On modified response, replace cache entry with fresh data.
4. Record timeline events for hit/miss/revalidation paths.

Implementation constraints:

1. Preserve request immutability when adding conditional headers.
2. Revalidation path must remain cancellation-safe.
3. Revalidation failures must not corrupt existing valid cache entries.
4. 304 merge logic must update cache-control and validator fields (`Cache-Control`, `ETag`, `Last-Modified`, `Expires`, `Date`, `Vary`).

---

## Verification Criteria

1. Cache hit path serves cached response and marks provenance (for example header/event).
2. `304` responses correctly reuse cached body and metadata.
3. `no-store` and equivalent directives prevent writes to cache.
4. Expired entries trigger revalidation or refetch behavior as configured.
5. Cache key logic prevents cross-user or cross-context collisions.
6. Equivalent URLs with reordered query parameters map to the same cache key.
7. Variant requests separated by `Vary` dimensions do not collide.
8. `max-age` vs `Expires` precedence is validated with targeted tests.
9. Path normalization cases (dot-segments, default ports, fragments) map to deterministic cache keys.
