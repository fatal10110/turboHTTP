# 2026-11 Cache stored representation (#61, #68, #155)

## What
- **#61** The buffered store path no longer shares the consumer's `HttpHeaders`. `CacheStoringHandler` clones the
  headers for the store before the consumer (whose instance becomes `UHttpResponse.Headers`) sees them. The tee path
  already cloned in the `TeeBodySource` ctor. Its trailers copy for the store is now also taken before the consumer
  receives the trailers.
- **#68** The 304 merge (`MergeNotModifiedEntry`) never writes Content-Length, Content-Encoding, Content-Range or
  Transfer-Encoding (`NotModifiedMergeExcludedHeaders`, RFC 9111 section 3.2). It drops the stored `Age` and `Date`:
  the 304 is the new response, so only its own Age/Date count, or the receive time as Date. Freshness from `Expires`
  is now `Expires - Date` (RFC 9111 section 4.2.1) instead of the absolute instant against the device clock. A
  stored response without a valid `Date` gets one with the receive time (RFC 9110 section 6.6.1). The Age is
  subtracted on the Expires branch too, and `AddClamped` bounds peer-controlled lifetimes against DateTime overflow.
  response_time is captured when the headers arrive (`CacheStoringHandler.OnResponseStartAsync`), not when the
  background store runs.
- **#155** Trailers are persisted. `CacheEntry.Trailers` is a new public property with a new public ctor overload;
  the 13-parameter ctor remains. `CacheEntry.Clone` and `MemoryCacheStorage.EnsureKey` copy the trailers, and
  `MemoryCacheStorage.EstimateEntrySizeBytes` counts them. They are replayed through `BufferedResponseBodySource`
  on HIT/STALE/REVALIDATED and carried over by the 304 merge. The buffered path reads the trailers before the
  consumer can consume or dispose the body; when they cannot be read, the response is delivered but not stored.
- Clock seam: `CacheInterceptor.UtcNowProvider` (internal, test only) is used for the lookup, store, merge and
  served Age. `MemoryCacheStorage` eviction sweeps keep the real clock.

## Files
- Runtime/Cache/CacheEntry.cs, CacheInterceptor.cs, CacheInterceptor.Parsing.cs, CacheStoringHandler.cs, MemoryCacheStorage.cs
- Tests/Runtime/Cache/CacheInterceptorTests.Representation.cs (+ .meta)

## Decisions
- #155: persist rather than skip. RFC 9111 section 3.1 allows storing trailers separately, and replaying them keeps
  checksum-style trailers working. A third-party `ICacheStorage` that rebuilds entries field by field must copy
  `Trailers` (documented on the property).
- #68 Content-Type: updated from the 304. RFC 9111 section 3.2 requires the update, and the cache does not parse
  the stored bytes, so the exception for processed content does not apply.
- #68 age: corrected_initial_age = corrected_age_value (the Age field). The conservative `max(apparent_age, ...)`
  would bring the device clock skew back, and RFC 9111 section 4.2.3 says the corrected_age_value "MAY be used".
  response_delay is still 0 because request_time is not recorded: follow-up #306.

## Verified
- `'./Tools~/ci/check.sh'`: OK.
- Unity PlayMode `--filter 'TurboHTTP\.Tests\.Cache\.|StreamingAllocationGateTests\.CacheRevalidation|IntegrationTests\..*Cache'`:
  75/75 passed, including the cache allocation gate. EditMode finds 0 tests for TurboHTTP.Tests.Runtime (expected).
- The 7 new tests failed against the origin/main `Runtime/Cache`, given only a no-op `UtcNowProvider` so they
  compile: 7/7 failed.
- Not run: the full Unity suites (periodic host-editor run), IL2CPP player, device.
