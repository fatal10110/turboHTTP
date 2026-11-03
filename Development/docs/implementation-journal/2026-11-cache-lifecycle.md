# 2026-11 Cache lifecycle: bounded variant index, dispose ownership, exact cache keys (#60, #71, #72)

Run w1102, lane `cache-lifecycle`, v1.1.

## What

- **#60, variant index bound.** `CacheInterceptor._variantIndex` (one `VariantBucket` per base key) is now an
  LRU bounded by `_variantIndexCapacity`. The bound is `MemoryCacheStorage.MaxEntries` (new internal property)
  when the policy uses the built-in storage, else `DefaultVariantIndexCapacity` (4096). A bucket is touched on
  register and on lookup (`GetSignatureSnapshot`). When a register pushes the index over the bound, the least
  recent bucket is dropped. Its Vary storage keys (everything except `baseKey|~`) are then removed from
  storage by a queued mutation on that base key (`RemoveEvictedVariantsCoreAsync`, skips keys that were
  re-registered in the meantime). The no-Vary key needs no removal: lookup falls back to it and
  `InvalidateBaseKeyCoreAsync` always removes it without the index.
- **#71, dispose ownership.** `RunQueuedMutationAsync` disposes an `IDisposable` mutation state when it skips the
  mutation on cancellation. `StoreResponseState` is now `IDisposable` (disposes the pooled `SegmentedBuffer`), so
  a store queued behind another mutation of the same key and cancelled by `Dispose()` returns its body.
  `Dispose()` now disposes `_backgroundWorkCancellation` after `Cancel()`. The token is captured once in the
  constructor (`_backgroundWorkToken`); the two `.Token` reads (`StartBackgroundRevalidation`,
  `QueueStoreResponse`) use it, so a call racing `Dispose()` sees a cancelled token rather than an
  `ObjectDisposedException`. The cancel-before-start part of the issue had already been fixed by #250.
- **#72, cache key.** `NormalizeUri` keeps the URI as sent. It applies only the RFC 3986 section 6.2.2/6.2.3
  equivalences: lowercase scheme and host, default port elided, percent-encoding (unreserved decoded, hex
  uppercased), and `remove_dot_segments` (section 5.2.4) on literal `.`/`..` only. It no longer sorts query
  parameters, drops empty path segments (`/a//b`) or drops empty query parameters (`a=1&&b=2`, trailing `&`).
  An empty query `?` is kept (section 6.2.3: the delimiter of an empty component is not removed for http).

## Files

- `Runtime/Cache/CacheInterceptor.cs`: fields, ctor, `Dispose`, `RunQueuedMutationAsync`, `StoreResponseState`,
  test accessors `PendingMutationCount`, `BackgroundWorkToken`.
- `Runtime/Cache/CacheInterceptor.Variants.cs`: LRU, eviction, `VariantIndexCount`, `VariantIndexCapacity`
  (internal test hook).
- `Runtime/Cache/CacheInterceptor.UriNormalization.cs`: `NormalizePath`, query path; `QueryPart` removed.
- `Runtime/Cache/MemoryCacheStorage.cs`: internal `MaxEntries`.
- Tests: `Tests/Runtime/Cache/CacheInterceptorTests.Lifecycle.cs`, `CacheInterceptorTests.KeyNormalization.cs`
  (new); `CacheInterceptorTests.VariantsAndAsync.cs` (`CacheInterceptor_NormalizesQueryParameterOrder` renamed
  to `CacheInterceptor_KeyNormalization_QueryParameterOrderIsSignificant`, now expects two origin requests).

## Decisions

- #60: an interceptor-side bound instead of an `ICacheStorage` eviction callback. A new interface member would
  break custom storages (public API), and the interceptor would still need a bound for storages that never
  call back. Dropping a bucket only loses the Vary accelerator. Its Vary entries are removed from storage so
  that an unsafe-method invalidation (RFC 9111 section 4.4) cannot miss them, and so that they cannot be served
  again once their signature is re-registered. For a custom storage holding Vary entries for more than
  4096 base keys, the least recently used ones are removed. That is always allowed for a cache and is
  noted in the PR.
- #72: the RFC wins over `Development/docs/phases/phase10/phase-10.4-cache-middleware.md` lines 25-27 ("sorted
  query parameters"). No opt-in aggressive mode: the issue allows dropping it, and an option would be new
  public API with no consumer.
- The brief flagged `NormalizePercentEncoding`'s `i + 2 < input.Length` as skipping a trailing `%XX`. It does not
  (index `i + 2` must be `< Length`). A test case (`?x=%41`) pins it.

## Not done / deferred

- P4-I5 (`SnapshotBodyForCache` / store `ToArray()`) and P4-W10 (`OrderBy` inside `_indexLock`) belong to the
  `cache-representation` lane (#61/#68/#155). They were not changed here.
- A store queued behind a custom storage call that ignores cancellation and never completes keeps its body
  until that call returns. This is inherent to per-key serialization.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity 2021.3.45f2 PlayMode, `--filter 'TurboHTTP\.Tests\.Cache\.'`: 66/66 passed (the
  fixture has no EditMode tests; EditMode with the same filter ran 0).
- Negative control: with the behavior hunks reverted (old `UriNormalization.cs`, no eviction, no state dispose, no
  CTS dispose) and the test accessors kept, all new tests and the renamed query-order test failed.
- Not run: the full Unity suites (host-editor periodic run), IL2CPP player builds, devices.
