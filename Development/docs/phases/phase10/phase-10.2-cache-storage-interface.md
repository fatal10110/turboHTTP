# Phase 10.2: Cache Storage Interface

**Depends on:** Phase 10.1
**Assembly:** `TurboHTTP.Cache`
**Files:** 1 new

---

## Step 1: Define `ICacheStorage`

**File:** `Runtime/Cache/ICacheStorage.cs`

Required behavior:

1. Define async APIs for `GetAsync`, `SetAsync`, `RemoveAsync`, and `ClearAsync`.
2. Expose observability APIs for entry count and total size.
3. Define behavior for missing/expired keys (return `null`).
4. Make storage swappable (memory now, disk later).
5. Keep v1 storage contract byte-array based for response snapshots while documenting future stream-oriented extension points.

Implementation constraints:

1. API contracts must document thread-safety expectations.
2. Methods must be cancellation-safe where cancellation token usage is introduced.
3. Storage API must not leak backend-specific details into middleware.
4. Interface should remain minimal to preserve backend flexibility.
5. Add an explicit forward-compatibility note for streaming backends (for example a future `IStreamingCacheStorage` adapter) to avoid breaking middleware contracts later.

---

## Verification Criteria

1. Interface covers all operations needed by cache middleware.
2. Contract docs clearly specify null/expiration behavior.
3. Existing middleware code can target `ICacheStorage` without backend conditionals.
4. Interface remains compatible with a future disk-cache backend.
5. Future stream-based cache evolution can be introduced without a breaking Phase 10 API rewrite.
