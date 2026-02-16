# Phase 16: Extended Capabilities and Resilience

**Milestone:** M4 (v1.x "differentiators")
**Dependencies:** Phase 14 prioritization
**Estimated Complexity:** Varies
**Critical:** No - Future enhancements

## Overview

This phase aggregates high-impact platform, protocol, security, and resilience features that are critical for specific use cases but are prioritized after the v1.0 core release.

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

### 3. WebSocket Support (High Priority)

**Goal:** Add WebSocket client alongside HTTP

**Use Cases:**
- Real-time multiplayer
- Live chat
- Push notifications

**Estimated Effort:** 2-3 weeks

**Complexity:** Medium

**Value:** High (expands use cases)

---

### 4. gRPC Support (Low Priority)

**Goal:** Support gRPC protocol

**Benefits:**
- Binary protocol, strong contracts, streaming.

**Challenges:**
- Protobuf compiler, IL2CPP compatibility.

**Estimated Effort:** 4-6 weeks

**Complexity:** Very High

**Value:** Low-Medium (niche)

---

### 5. GraphQL Client (Medium Priority)

**Goal:** Add GraphQL query builder and client

**Estimated Effort:** 1-2 weeks

**Complexity:** Low-Medium

**Value:** Medium

---

### 6. Parallel Request Helpers (Low Priority)

**Goal:** Simplify common parallel request patterns (Batch, Race, AllOrNone).

**Estimated Effort:** 1 week

**Complexity:** Low

**Value:** Low-Medium

---

### 7. Security & Privacy Hardening (High Priority)

**Goal:** Make "safe by default" behavior explicit and configurable.

**Focus Areas:**
- Redaction of sensitive headers/logs.
- Secure default cache partitioning.
- TLS pinning hooks.

**Estimated Effort:** 1-2 weeks

**Complexity:** Medium

**Value:** High

---

## Prioritization Matrix

| Feature | Priority | Effort | Complexity | Value | Version |
|---------|----------|--------|------------|-------|---------|
| Rate Limiting | High | 2w | Medium | High | v1.1 |
| WebGL Support | High | 2-3w | Medium | High | v1.1 |
| Security & Privacy | High | 1-2w | Medium | High | v1.1 |
| WebSocket | High | 2-3w | Medium | High | v1.2 |
| GraphQL | Medium | 1-2w | Low | Medium | v1.3 |
| Parallel Helpers | Low | 1w | Low | Low | v1.x |
| gRPC | Low | 4-6w | Very High | Low | v2.0? |

## Recommended Roadmap

### v1.1 (Q1 after v1.0)
- Rate Limiting (Token Bucket)
- WebGL support
- Security & privacy hardening

### v1.2 (Q2)
- WebSocket support

### v1.3 (Q3)
- GraphQL client

### v1.x backlog
- Parallel request helpers

### v2.0 (Q4+)
- gRPC support

## Notes

- Keep this phase synchronized with Phase 14 roadmap decisions.
- Re-prioritize based on post-v1.0 adoption data.
