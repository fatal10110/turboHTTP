# Phase 10.3: Memory Cache Storage (LRU + TTL)

**Depends on:** Phase 10.2
**Assembly:** `TurboHTTP.Cache`
**Files:** 1 new

---

## Step 1: Implement `MemoryCacheStorage`

**File:** `Runtime/Cache/MemoryCacheStorage.cs`

Required behavior:

1. Store entries in memory with bounded `maxEntries` and `maxSizeBytes`.
2. Enforce LRU eviction when limits are exceeded.
3. Remove expired entries on read and during eviction passes.
4. Return count and size metrics from `GetCountAsync` and `GetSizeAsync`.

Implementation constraints:

1. Use one async-safe critical section for dictionary, LRU list, and size counters (single `SemaphoreSlim(1,1)` or equivalent).
2. Do not block async methods with `.Wait()` or `.Result`.
3. Never `await` while holding the critical section; gather mutation inputs first, then perform lock-protected state changes synchronously.
4. Do not perform nested cache calls while holding the lock; mutate all related state in one guarded section.
5. Size accounting must be transactional: compute entry size before lock, then evict/update counters in the same critical section.
6. Use a deterministic size formula: `entrySize = bodyBytes + headerBytes + fixedMetadataBytes`.
7. Document and keep fixed metadata estimate constant (for example `1024` bytes per entry).
8. Normalize invalid constructor inputs (non-positive limits) with explicit validation.

---

## Verification Criteria

1. Writes beyond capacity evict least-recently-used entries.
2. Size limit eviction triggers correctly when large entries are inserted.
3. Expired entries are not returned from `GetAsync`.
4. Concurrent access does not corrupt cache state.
5. `ClearAsync` resets all state (entries, LRU list, size counters).
6. Concurrent random read/write/evict stress with forced async yields preserves map/list consistency and size invariants.
7. Deadlock stress tests (forced contention + async yields) complete without hangs.
