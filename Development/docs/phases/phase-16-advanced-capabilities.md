# Phase 16: Extended Capabilities and Resilience

**Milestone:** M4 (v1.x "differentiators")
**Dependencies:** Phase 14 prioritization
**Estimated Complexity:** Medium
**Critical:** No - Future enhancements

## Overview

This phase aggregates focused, self-contained features that enhance the TurboHTTP platform after the v1.0 core release. Larger protocol-level features (WebSocket, gRPC) and deep refactoring (async runtime, content handlers) have been extracted to their own dedicated phases.

## Features

### 1. Rate Limiting (High Priority)

**Goal:** Client-side rate limiting to prevent server overload and handle 429 Too Many Requests responses gracefully. (Deferred from Phase 10).

**Use Cases:**
- Respecting public API limits (e.g., GitHub, Twitter)
- Preventing accidental DoS during development
- Smoothing bursty traffic

**Implementation:**

1. **`RateLimitConfig`**:
   - Max requests per window (e.g., 60 req / 1 min).
   - Support per-host policies (different limits for different domains).
   - Support global overrides.

2. **`TokenBucket` Algorithm**:
   - Thread-safe, lock-free implementation using `Interlocked`.
   - Deterministic refill based on `Stopwatch` timestamps.
   - Support for waiting (async delay) or failing fast when limit reached.

3. **`RateLimitMiddleware`**:
   - Integrates `TokenBucket` into the pipeline.
   - Handles `Retry-After` headers from 429 responses to dynamic adjust limits (optional advanced feature).

```csharp
var policy = new RateLimitPolicy
{
    MaxRequests = 100,
    TimeWindow = TimeSpan.FromMinutes(1),
    PerHost = true
};
client.Options.Middlewares.Add(new RateLimitMiddleware(policy));
```

**Estimated Effort:** 2 weeks

**Complexity:** Medium (Concurrency correctness is key)

**Value:** High (Essential for production apps using third-party APIs)

---

### 2. WebGL Support (High Priority)

**Goal:** Make TurboHTTP work in WebGL builds

**Approach:** Implement a `.jslib` JavaScript plugin that wraps the browser `fetch()` API, with a C# `WebGLBrowserTransport : IHttpTransport` that calls into it via `[DllImport("__Internal")]`.

**Architecture:**
```
IHttpTransport
├── RawSocketTransport          ← Desktop/Mobile (Phase 3/3B)
└── WebGLBrowserTransport       ← WebGL: fetch() API via .jslib
```

**Estimated Effort:** 2-3 weeks

**Complexity:** Medium

**Value:** High (expands platform support)

---

### 3. GraphQL Client (Medium Priority)

**Goal:** Add GraphQL query builder and client

**Estimated Effort:** 1-2 weeks

**Complexity:** Low-Medium

**Value:** Medium

---

### 4. Parallel Request Helpers (Low Priority)

**Goal:** Simplify common parallel request patterns (Batch, Race, AllOrNone).

**Estimated Effort:** 1 week

**Complexity:** Low

**Value:** Low-Medium

---

### 5. Security & Privacy Hardening (High Priority)

**Goal:** Make "safe by default" behavior explicit and configurable.

**Focus Areas:**
- Redaction of sensitive headers/logs.
- Secure default cache partitioning.
- TLS pinning hooks.

**Estimated Effort:** 1-2 weeks

**Complexity:** Medium

**Value:** High

---

## Extracted to Dedicated Phases

The following features were originally part of Phase 16 but have been promoted to their own phases due to scope:

| Feature | New Phase | Reason |
|---------|-----------|--------|
| WebSocket Support | [Phase 18](phase-18-websocket-client.md) | Full new protocol (RFC 6455), comparable to Phase 3B HTTP/2 |
| Async Runtime Refactor + UniTask | [Phase 19](phase-19-async-runtime-refactor.md) | Deep cross-cutting architectural refactor |
| Advanced Content Handlers | [Phase 20](phase-20-advanced-content-handlers.md) | Aggregate ~6-8 weeks across 5+ handlers |
| gRPC Support | [Phase 21](phase-21-grpc-client.md) | Largest item in roadmap, v2.0 feature |

---

## Prioritization Matrix

| Feature | Priority | Effort | Complexity | Value | Version |
|---------|----------|--------|------------|-------|---------|
| Rate Limiting | High | 2w | Medium | High | v1.1 |
| WebGL Support | High | 2-3w | Medium | High | v1.1 |
| Security & Privacy | High | 1-2w | Medium | High | v1.1 |
| GraphQL | Medium | 1-2w | Low | Medium | v1.3 |
| Parallel Helpers | Low | 1w | Low | Low | v1.x |

### Extracted phases (see dedicated docs):

| Feature | Phase | Effort | Version |
|---------|-------|--------|---------|
| WebSocket Client | Phase 18 | 3-4w | v1.2 |
| Async Runtime Refactor | Phase 19 | 3-4w | v1.2 |
| Advanced Content Handlers | Phase 20 | 6-8w | v1.2+ |
| gRPC Client | Phase 21 | 5-7w | v2.0 |

## Recommended Roadmap

### v1.1 (Q1 after v1.0)
- Rate Limiting (Token Bucket)
- WebGL support
- Security & privacy hardening

### v1.2 (Q2)
- Async runtime refactor (Phase 19)
- WebSocket client (Phase 18)
- Advanced content handlers — first batch (Phase 20)

### v1.3 (Q3)
- GraphQL client
- Advanced content handlers — second batch (Phase 20)

### v1.x backlog
- Parallel request helpers

### v2.0 (Q4+)
- gRPC client (Phase 21)

## Notes

- Keep this phase synchronized with Phase 14 roadmap decisions.
- Re-prioritize based on post-v1.0 adoption data.
- Advanced content handlers depend on Phase 15 decoder abstractions and pipeline hardening.
