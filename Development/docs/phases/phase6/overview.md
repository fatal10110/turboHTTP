# Phase 6 Implementation Plan — Overview

Phase 6 is broken into 6 sub-phases executed sequentially. Each sub-phase is self-contained with its own files, verification criteria, and review checkpoints.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [6.1](phase-6.1-pooling-primitives.md) | Pooling Primitives (ObjectPool + ByteArrayPool) | 2 new | Phase 5 |
| [6.2](phase-6.2-concurrency-controls.md) | Concurrency Limiter + Middleware | 2 new | 6.1 |
| [6.3](phase-6.3-request-queue.md) | Priority Request Queue | 1 new | 6.1 |
| [6.4](phase-6.4-timeline-optimization.md) | RequestContext Timeline Optimization | 1 modified | 6.1 |
| [6.5](phase-6.5-disposal-hardening.md) | UHttpClient Disposal Hardening | 1 modified | 6.2, 6.3, 6.4 |
| [6.6](phase-6.6-stress-and-benchmarks.md) | Stress Tests and Performance Gates | 1 new | 6.2, 6.3, 6.4, 6.5 |

## Dependency Graph

```text
Phase 5 (done)
    └── 6.1 Pooling Primitives
         ├── 6.2 Concurrency Limiter + Middleware
         ├── 6.3 Priority Request Queue
         └── 6.4 RequestContext Timeline Optimization
              └── 6.5 UHttpClient Disposal Hardening (also depends on 6.3)
                   └── 6.6 Stress Tests and Performance Gates
```

Sub-phases 6.2, 6.3, and 6.4 can be implemented in parallel once 6.1 is complete. Sub-phase 6.6 validates the full performance-hardening set.

## Existing Foundation (Phases 3B + 4 + 5)

### Existing Types Used in Phase 6

| Type | Key APIs for Phase 6 |
|------|----------------------|
| `UHttpClient` | `SendAsync`, middleware chain ownership, disposal lifecycle |
| `RequestContext` | `RecordEvent`, timeline storage, elapsed tracking |
| `IHttpMiddleware` | `InvokeAsync(...)` pipeline integration point |
| `HttpPipelineDelegate` | next delegate used by middleware |
| `MockTransport` | deterministic transport for stress tests |
| `UHttpClientOptions` | middleware registration and client configuration |

### Assembly Structure

| Assembly | References | autoReferenced | Notes |
|----------|-----------|----------------|-------|
| `TurboHTTP.Core` | none | true | RequestContext and UHttpClient modifications |
| `TurboHTTP.Performance` | Core | false | Performance primitives and middleware |
| `TurboHTTP.Tests.Runtime` | All runtime modules | false | Stress and benchmark validation |

## Cross-Cutting Design Decisions

1. **Keep pooling local and explicit:** pools are internal implementation details; public API remains stable.
2. **Bound every pool:** no unbounded bucket growth; enforce per-bucket caps.
3. **Concurrency should be host-scoped:** middleware limit complements transport-level connection pooling.
4. **Avoid hidden global state leaks:** disposal paths must clean semaphores, queues, and pooled objects where applicable.
5. **Timeline optimization must preserve semantics:** event order and timestamps must remain unchanged.
6. **Performance gates are enforced in tests:** throughput/memory checks prevent regressions.

## All Files (6 new, 2 modified)

| # | Action | Path | Assembly |
|---|--------|------|----------|
| 1 | Create | `Runtime/Performance/ObjectPool.cs` | Performance |
| 2 | Create | `Runtime/Performance/ByteArrayPool.cs` | Performance |
| 3 | Create | `Runtime/Performance/ConcurrencyLimiter.cs` | Performance |
| 4 | Create | `Runtime/Performance/ConcurrencyMiddleware.cs` | Performance |
| 5 | Create | `Runtime/Performance/RequestQueue.cs` | Performance |
| 6 | Modify | `Runtime/Core/RequestContext.cs` | Core |
| 7 | Modify | `Runtime/Core/UHttpClient.cs` | Core |
| 8 | Create | `Tests/Runtime/Performance/StressTests.cs` | Tests |

## Post-Implementation

1. Run both specialist agent reviews (unity-infrastructure-architect, unity-network-architect).
2. Run runtime test suite with performance-focused categories.
3. Create or update implementation journal entry for Phase 6.
4. Update `CLAUDE.md` development status for Phase 6 completion.
