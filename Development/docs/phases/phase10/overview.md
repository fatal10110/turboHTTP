# Phase 10 Implementation Plan - Overview

Phase 10 implementation is broken into 7 active sub-phases. Cache track (10.1-10.4) and middleware hardening track (10.8-10.10) can proceed in parallel after Phase 9. Rate limiting tasks (10.5-10.7) are deferred to the [Phase 14 roadmap](../phase-14-future.md).

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [10.1](phase-10.1-cache-entry-model.md) | Cache Entry Model | 1 new | Phase 9 |
| [10.2](phase-10.2-cache-storage-interface.md) | Cache Storage Interface | 1 new | 10.1 |
| [10.3](phase-10.3-memory-cache-storage.md) | Memory Cache Storage (LRU + TTL) | 1 new | 10.2 |
| [10.4](phase-10.4-cache-middleware.md) | Cache Middleware and Revalidation | 1 new | 10.3 |
| [10.8](phase-10.8-redirect-middleware.md) | Redirect Middleware | 1 new | Phase 9 |
| [10.9](phase-10.9-cookie-middleware.md) | Cookie Middleware | 2 new | 10.8 |
| [10.10](phase-10.10-streaming-transport-improvements.md) | Streaming Transport Improvements | 1 new, 2 modified | Phase 9 |

## Deferred Task Index

These tasks are not part of active Phase 10 implementation scope.

| Task | Name | Status | Reference |
|---|---|---|---|
| 10.5 | Rate Limit Policy Model | Deferred to Phase 14 | [phase-10.5-rate-limit-policy.md](phase-10.5-rate-limit-policy.md) |
| 10.6 | Token Bucket Limiter | Deferred to Phase 14 | [phase-10.6-token-bucket.md](phase-10.6-token-bucket.md) |
| 10.7 | Rate Limit Middleware and Tests | Deferred to Phase 14 | [phase-10.7-rate-limit-middleware.md](phase-10.7-rate-limit-middleware.md) |

## Dependency Graph

```text
Phase 9 (done)
    ├── 10.1 Cache Entry Model
    │    └── 10.2 Cache Storage Interface
    │         └── 10.3 Memory Cache Storage
    │              └── 10.4 Cache Middleware and Revalidation
    ├── 10.8 Redirect Middleware
    │    └── 10.9 Cookie Middleware
    └── 10.10 Streaming Transport Improvements
```

## Existing Foundation (Phases 4 + 6 + 7 + 9)

### Existing Types Used in Phase 10

| Type | Key APIs for Phase 10 |
|------|----------------------|
| `IHttpMiddleware` | middleware extension point for cache behavior |
| `UHttpRequest` / `UHttpResponse` | cache key and response materialization |
| `HttpHeaders` | cache metadata, conditional request headers |
| `RequestContext` | events for cache hit/miss/revalidation |
| `UHttpClientOptions` | redirect settings (`FollowRedirects`, `MaxRedirects`) |
| `Http11ResponseParser` | HTTP/1.1 response parsing path to optimize |
| `ConcurrencyMiddleware` | interaction with queueing/backpressure behavior |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Cache` | Core | false | cache model, storage, middleware |
| `TurboHTTP.Middleware` | Core | false | redirect and cookie middleware |
| `TurboHTTP.Transport` | Core | false | HTTP/1.1 parser improvements |
| `TurboHTTP.Tests.Runtime` | runtime modules | false | cache, middleware, and transport tests |

## Cross-Cutting Design Decisions

1. Cache behavior must be conservative by default for privacy and correctness.
2. Cache memory usage must be bounded by entry count and bytes.
3. Revalidation must prefer protocol-correct conditional requests (`ETag`, `Last-Modified`).
4. Redirect handling must follow RFC semantics and strip sensitive headers on cross-origin hops.
5. Cookie behavior must be RFC 6265-compliant and thread-safe under concurrent request flows.
6. Cache variant handling must respect content-encoding negotiation via `Vary: Accept-Encoding`.
7. Streaming parser changes must preserve correctness while removing byte-by-byte hot paths.
8. Deterministic tests cover core semantics; external behavior is optional and isolated.

## All Files (11 new, 3 modified)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Cache/CacheEntry.cs` | Cache |
| 2 | Create | `Runtime/Cache/ICacheStorage.cs` | Cache |
| 3 | Create | `Runtime/Cache/MemoryCacheStorage.cs` | Cache |
| 4 | Create | `Runtime/Cache/CacheMiddleware.cs` | Cache |
| 5 | Create | `Tests/Runtime/Cache/CacheMiddlewareTests.cs` | Tests |
| 6 | Create | `Runtime/Middleware/RedirectMiddleware.cs` | Middleware |
| 7 | Create | `Tests/Runtime/Middleware/RedirectMiddlewareTests.cs` | Tests |
| 8 | Create | `Runtime/Middleware/CookieJar.cs` | Middleware |
| 9 | Create | `Runtime/Middleware/CookieMiddleware.cs` | Middleware |
| 10 | Create | `Tests/Runtime/Middleware/CookieMiddlewareTests.cs` | Tests |
| 11 | Modify | `Runtime/Transport/Http1/Http11ResponseParser.cs` | Transport |
| 12 | Modify | `Tests/Runtime/Transport/Http11ResponseParserTests.cs` | Tests |
| 13 | Create | `Tests/Runtime/Transport/Http11ResponseParserPerformanceTests.cs` | Tests |
| 14 | Modify | `Tests/Runtime/Integration/IntegrationTests.cs` | Tests |

## Post-Implementation

1. Run deterministic cache and middleware test suites.
2. Run HTTP/1.1 parser regression and performance guard tests.
3. Validate middleware composition with retry/concurrency ordering and redirect/cookie interactions.
4. Confirm no unbounded growth in cache or cookie state under stress.
5. Add/extend full-stack integration scenario: request -> redirect -> set-cookie -> cache miss -> revalidate -> 304.
6. Run specialist reviews before advancing to Phase 11.
