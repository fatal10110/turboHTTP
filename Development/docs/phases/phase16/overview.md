# Phase 16 Implementation Plan - Overview

Phase 16 is split into 5 sub-phases. Rate Limiting, WebGL Support, and Security & Privacy Hardening are high-priority v1.1 features. GraphQL Client and Parallel Request Helpers are lower-priority and can be deferred to later releases.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [16.1](phase-16.1-rate-limiting.md) | Rate Limiting (Token Bucket + Middleware) | 5 new, 1 modified | Phase 4 (Pipeline) |
| [16.2](phase-16.2-webgl-support.md) | WebGL Support (Browser Fetch Transport) | 4 new, 1 modified | Phase 3 (Transport) |
| [16.3](phase-16.3-security-privacy-hardening.md) | Security & Privacy Hardening | 4 new, 3 modified | Phase 6, Phase 10 |
| [16.4](phase-16.4-graphql-client.md) | GraphQL Client | 4 new, 0 modified | Phase 5 (JSON) |
| [16.5](phase-16.5-parallel-request-helpers.md) | Parallel Request Helpers | 3 new, 0 modified | Phase 4 (Pipeline) |

## Dependency Graph

```text
Phase 4 (Pipeline — done)
    ├── 16.1 Rate Limiting
    └── 16.5 Parallel Request Helpers

Phase 3 (Transport — done)
    └── 16.2 WebGL Support

Phase 5 (JSON — done) + Phase 4
    └── 16.4 GraphQL Client

Phase 6 (Performance — done) + Phase 10 (Advanced Middleware — done)
    └── 16.3 Security & Privacy Hardening

No inter-dependencies between 16.1–16.5; all sub-phases can run in parallel.
```

Sub-phases 16.1 through 16.5 have no mutual dependencies — they can all proceed in parallel once their upstream phases are complete (all are already done).

## Existing Foundation

### Existing Types Used in Phase 16

| Type | Key APIs for Phase 16 |
|------|----------------------|
| `IHttpMiddleware` | Pipeline delegate pattern for Rate Limiting middleware |
| `IHttpTransport` | Transport interface for WebGL browser fetch transport |
| `HttpTransportFactory` | Registration pattern for WebGL transport auto-wiring |
| `UHttpRequestBuilder` | Extension method surface for GraphQL and parallel helper APIs |
| `UHttpClient` | Client API for parallel request combinators |
| `RequestContext` | Timeline events and cross-middleware state coordination |
| `LoggingMiddleware` | Existing sensitive header redaction to extend |
| `CacheMiddleware` | Cache storage to add partitioning policy |
| `ConcurrencyLimiter` | Reference pattern for lock-free rate limiting |
| `HttpHeaders` | Header collection for Retry-After parsing |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.RateLimit` | Core | false | Token bucket + rate limit middleware |
| `TurboHTTP.WebGL` | Core | false | Browser fetch transport, excludePlatforms except WebGL |
| `TurboHTTP.Security` | Core | false | Redaction, TLS pinning hooks, cache partitioning policy |
| `TurboHTTP.GraphQL` | Core, JSON | false | GraphQL query builder and client extensions |
| `TurboHTTP.Parallel` | Core | false | Batch, Race, AllOrNone combinators |

## Prioritization Matrix

| Sub-Phase | Priority | Effort | Complexity | Value | Version |
|-----------|----------|--------|------------|-------|---------|
| 16.1 Rate Limiting | High | 2w | Medium | High | v1.1 |
| 16.2 WebGL Support | High | 2-3w | Medium | High | v1.1 |
| 16.3 Security & Privacy | High | 1-2w | Medium | High | v1.1 |
| 16.4 GraphQL Client | Medium | 1-2w | Low-Medium | Medium | v1.3 |
| 16.5 Parallel Helpers | Low | 1w | Low | Low-Medium | v1.x |

## Extracted to Dedicated Phases

The following features were originally part of Phase 16 but have been promoted to their own phases due to scope:

| Feature | New Phase | Reason |
|---------|-----------|--------|
| WebSocket Support | [Phase 18](../phase-18-websocket-client.md) | Full new protocol (RFC 6455) |
| Async Runtime Refactor + UniTask | [Phase 19](../phase-19-async-runtime-refactor.md) | Deep cross-cutting architectural refactor |
| Advanced Content Handlers | [Phase 20](../phase-20-advanced-content-handlers.md) | Aggregate ~6-8 weeks across 5+ handlers |
| gRPC Support | [Phase 21](../phase-21-grpc-client.md) | Largest item in roadmap, v2.0 feature |

## Cross-Cutting Design Decisions

1. All Phase 16 features integrate via existing extension points (middleware pipeline, transport interface, builder extensions) — no Core modifications required.
2. Each sub-phase produces its own assembly with `autoReferenced: false` and dependency only on `TurboHTTP.Core` (GraphQL additionally references JSON).
3. Thread safety must be verified under IL2CPP 32-bit constraints (use `Interlocked` for atomic operations, avoid `Volatile`).
4. WebGL transport must gracefully degrade when socket-based features are unavailable.
5. All new middleware must record timeline events in `RequestContext` for observability.
6. Security defaults must be safe-by-default with opt-out, not opt-in.

## All Files (20 new, 5 modified planned)

| Area | Planned New Files | Planned Modified Files |
|---|---|---|
| 16.1 Rate Limiting | 5 | 1 |
| 16.2 WebGL Support | 4 | 1 |
| 16.3 Security & Privacy | 4 | 3 |
| 16.4 GraphQL Client | 4 | 0 |
| 16.5 Parallel Helpers | 3 | 0 |

## Post-Implementation

1. Run full middleware pipeline integration tests with rate limiting under sustained load.
2. Validate WebGL transport in browser environment with HTTP/1.1 and CORS scenarios.
3. Verify security hardening defaults do not break existing client configurations.
4. Confirm GraphQL client works with standard GraphQL endpoints (GitHub API, etc.).
5. Validate parallel request helpers under cancellation and partial failure scenarios.
6. Gate Phase 16 completion on green CI plus documented API surface review.
