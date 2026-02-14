# Phase 10 Implementation Plan - Overview

Phase 10 is broken into 7 sub-phases. Cache track (10.1-10.4) and rate-limit track (10.5-10.6) can progress in parallel, then 10.7 integrates and validates policy behavior.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [10.1](phase-10.1-cache-entry-model.md) | Cache Entry Model | 1 new | Phase 9 |
| [10.2](phase-10.2-cache-storage-interface.md) | Cache Storage Interface | 1 new | 10.1 |
| [10.3](phase-10.3-memory-cache-storage.md) | Memory Cache Storage (LRU + TTL) | 1 new | 10.2 |
| [10.4](phase-10.4-cache-middleware.md) | Cache Middleware and Revalidation | 1 new | 10.3 |
| [10.5](phase-10.5-rate-limit-policy.md) | Rate Limit Policy Model | 1 new | Phase 9 |
| [10.6](phase-10.6-token-bucket.md) | Token Bucket Limiter | 1 new | 10.5 |
| [10.7](phase-10.7-rate-limit-middleware.md) | Rate Limit Middleware and Tests | 3 new | 10.4, 10.6 |

## Dependency Graph

```text
Phase 9 (done)
    ├── 10.1 Cache Entry Model
    │    └── 10.2 Cache Storage Interface
    │         └── 10.3 Memory Cache Storage
    │              └── 10.4 Cache Middleware and Revalidation
    └── 10.5 Rate Limit Policy Model
         └── 10.6 Token Bucket Limiter
              └── 10.7 Rate Limit Middleware and Tests (also depends on 10.4)
```

## Existing Foundation (Phases 4 + 6 + 7 + 9)

### Existing Types Used in Phase 10

| Type | Key APIs for Phase 10 |
|------|----------------------|
| `IHttpMiddleware` | middleware extension point for cache and rate limiting |
| `UHttpRequest` / `UHttpResponse` | cache key and response materialization |
| `HttpHeaders` | cache metadata, conditional request headers |
| `RequestContext` | events for hit/miss/revalidation/rate-limit waiting |
| `ConcurrencyMiddleware` | interaction with queueing/backpressure behavior |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Cache` | Core | false | cache model, storage, middleware |
| `TurboHTTP.RateLimit` | Core | false | policy, token bucket, middleware |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | cache and rate-limit tests |

## Cross-Cutting Design Decisions

1. Cache behavior must be conservative by default for privacy and correctness.
2. Cache memory usage must be bounded by entry count and bytes.
3. Revalidation must prefer protocol-correct conditional requests (`ETag`, `Last-Modified`).
4. Rate-limit algorithms must be cancellation-safe and free of busy-wait blocking.
5. Per-host limiter maps must be bounded to prevent long-session growth.
6. Deterministic tests cover core semantics; external behavior is optional and isolated.

## All Files (9 new)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Cache/CacheEntry.cs` | Cache |
| 2 | Create | `Runtime/Cache/ICacheStorage.cs` | Cache |
| 3 | Create | `Runtime/Cache/MemoryCacheStorage.cs` | Cache |
| 4 | Create | `Runtime/Cache/CacheMiddleware.cs` | Cache |
| 5 | Create | `Runtime/RateLimit/RateLimitConfig.cs` | RateLimit |
| 6 | Create | `Runtime/RateLimit/TokenBucket.cs` | RateLimit |
| 7 | Create | `Runtime/RateLimit/RateLimitMiddleware.cs` | RateLimit |
| 8 | Create | `Tests/Runtime/Cache/CacheMiddlewareTests.cs` | Tests |
| 9 | Create | `Tests/Runtime/RateLimit/TokenBucketTests.cs` | Tests |

## Post-Implementation

1. Run deterministic cache and rate-limit test suites.
2. Validate middleware composition with retry/concurrency middleware ordering.
3. Confirm no unbounded growth in cache or host limiter maps under stress.
4. Run specialist reviews before advancing to Phase 11.
