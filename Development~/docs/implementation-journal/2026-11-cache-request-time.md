# 2026-11 Cache age corrected by response_delay (#306)

## What
- RFC 9111 section 4.2.3: `corrected_age_value = age_value + response_delay`, with
  `response_delay = response_time - request_time`. The cache recorded response_time only (#68), so response_delay
  was always 0 and a stored response looked younger than it was by the round-trip time.
- request_time is now captured on the interceptor clock (`CacheInterceptor.GetUtcNow`, the `UtcNowProvider` test seam)
  when the request is dispatched:
  - miss path: in the `CacheStoringHandler` ctor, which runs right before `next`;
  - revalidation: in the `RevalidationHandler` ctor, right before the conditional request is dispatched. It is
    passed to the 304 merge and to the `CacheStoringHandler` that a 200 after revalidation builds after the response
    has arrived (its own ctor time would give a zero delay).
- The time travels with response_time: `QueueStoreResponse` / `StoreResponseState` / `StoreResponseAsync` /
  `PrepareEntryForStorage` (buffered path) and `TeeBodySource` (streaming path). `MergeNotModifiedEntry` takes the
  304's request_time and response_time (captured when `CompleteNotModifiedRevalidationAsync` starts).
- `ApplyResponseDelay` (`CacheInterceptor.Parsing.cs`) folds `max(0, response_time - request_time)` into the stored
  `Age` before `ResolveExpiry`. The stored Age already meant corrected_initial_age to `ResolveExpiry` and
  `ComputeCurrentAgeSeconds`, so freshness and the served Age both follow without further changes.

## Files
- Runtime/Cache/CacheStoringHandler.cs, CacheInterceptor.cs, CacheInterceptor.StreamingDispatch.cs, CacheInterceptor.Parsing.cs
- Tests/Runtime/Cache/CacheInterceptorTests.RequestTime.cs (+ .meta)

## Decisions
- Persist the correction in the stored `Age` header instead of a new `CacheEntry` member: no public API change, no
  `Clone`/`EnsureKey`/size-estimate/third-party `ICacheStorage` updates. Entries stored before this change hold the
  uncorrected Age, so they keep the old behaviour (response_delay 0), as the lane note asks. A store without a
  request_time (the internal `QueueStoreResponse` default, the unused 3-parameter `PrepareEntryForStorage`) adds
  nothing either.
- response_delay is clamped at 0 (issue step 3): a device clock stepping back between send and receipt never makes a
  response younger.
- Whole seconds, rounded down, like `Age` (delta-seconds) and resident_time in `ComputeCurrentAgeSeconds`. Rounding
  up would age every response by a full second on sub-second round trips (a `max-age=1` response would never be
  fresh).
- Still no `max(apparent_age, corrected_age_value)`: RFC 9111 section 4.2.3 lets corrected_age_value alone be the
  corrected_initial_age, and apparent_age would bring device clock skew back (decided in the cache-representation
  lane).
- On a 304 the stored Age is still dropped (#68); the 304's own Age is corrected by the conditional request's delay
  (issue step 4).

## Deferred
- None.

## Verification
- `'./Tools~/ci/check.sh'`: OK.
- New tests in `CacheInterceptorTests.RequestTime.cs`: the acceptance case (buffered: `Age: 10`, `max-age=60`, 5 s
  delay, fresh for 45 s, served Age from 15), the same on the streaming tee path, a 304 with `Age: 10` and a 3 s
  delay (Age from 13, fresh 47 s), a 200 after revalidation (delay from the conditional request), a clock stepping
  back (Age stays 10) and a store without request_time (old behaviour). On the old code each case except the last two
  serves the uncorrected Age and stays fresh past the corrected lifetime.
- Scoped Unity PlayMode run: see the PR "Local gates" section. IL2CPP players and devices were not run (nothing
  platform-specific changed).
