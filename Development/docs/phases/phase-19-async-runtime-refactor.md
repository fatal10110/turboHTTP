# Phase 19: Async Runtime Refactor with Optional UniTask Adapters

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 4 (Pipeline), Phase 3B (HTTP/2), Phase 6 (Performance), Phase 15 (Runtime Hardening)
**Estimated Complexity:** High
**Estimated Effort:** 3-4 weeks
**Critical:** No - Performance optimization

## Overview

Reduce async overhead in hot paths (allocation and scheduling churn) by migrating internal execution paths to `ValueTask`-first patterns, while keeping `TurboHTTP.Core` free of third-party runtime dependencies. The existing public `Task`-based API remains source-compatible for v1.x consumers. UniTask support is provided as an optional adapter module in a separate assembly.

This is a cross-cutting architectural refactor — similar in nature to Phase 6 (Performance) — not a feature addition. (Note: For absolute zero-allocation strategies including buffer pooling and non-blocking SAEA socket wrappers targeting extreme concurrency, see **Phase 19a: Extreme Performance & Zero-Allocation Networking**).

## Design Constraints

- `TurboHTTP.Core` must **not** reference `com.cysharp.unitask`.
- Existing public `Task` API remains source-compatible for v1.x consumers.
- UniTask support, if provided, must remain an optional adapter module.

## Tasks

### Task 19.1: Internal ValueTask Abstraction Layer

**Goal:** Introduce `ValueTask`-first execution paths for internal pipeline and transport operations.

**Deliverables:**
- Define internal V2 interfaces/delegates: `ValueTask<UHttpResponse>` variants
- Create boundary adapters that bridge `Task ↔ ValueTask` at public API surface
- Adapter shims so existing middleware/transport implementations continue to work during incremental migration
- Document migration guide for custom middleware authors

**Estimated Effort:** 1 week

---

### Task 19.2: Pipeline & Transport Migration

**Goal:** Migrate the request pipeline dispatch path and transport layer to the new ValueTask-first interfaces.

**Deliverables:**
- Middleware hop chain uses `ValueTask` internally
- Transport send/receive paths return `ValueTask`
- Connection pool acquisition returns `ValueTask`
- Existing middleware implementations adapted via shims (no breaking changes)

**Estimated Effort:** 1 week

---

### Task 19.3: HTTP/2 Hot-Path Refactor

**Goal:** Eliminate `TaskCompletionSource` overhead in HTTP/2 stream lifecycle.

**Deliverables:**
- HTTP/2 stream completion/wait paths use pooled `ValueTaskSource` or equivalent
- Request pipeline dispatch path (middleware hop overhead) optimized
- Connection racing / delay coordination (Happy Eyeballs) allocation-reduced
- Other frequently-called internal async helpers identified by profiling

**Estimated Effort:** 1 week

---

### Task 19.4: Optional UniTask Adapter Module

**Goal:** Provide UniTask-based API surface as a separate, optional assembly.

**Deliverables:**
- Separate `TurboHTTP.UniTask` assembly (gated by asmdef `versionDefines`)
- Extension methods: `GetAsync().AsUniTask()`, `SendAsync().AsUniTask()`
- Adapters map to the new internal fast path without Core → UniTask dependency
- `PlayerLoopTiming` integration for frame-aware scheduling

**Estimated Effort:** 3-4 days

---

### Task 19.5: Benchmarks & Regression Validation

**Goal:** Prove the refactor delivers measurable improvement without behavior regressions.

**Deliverables:**
- Before/after allocation benchmarks under HTTP/1.1 and HTTP/2
- Throughput benchmarks (requests/sec) under varying concurrency
- Stress tests for cancellation, timeout, and race-heavy scenarios
- No regression in API behavior, error mapping, or middleware ordering
- CI gate comparing allocation counts against baseline

**Estimated Effort:** 3-4 days

---

## Prioritization Matrix

| Task | Priority | Effort | Dependencies |
|------|----------|--------|--------------|
| 19.1 ValueTask Abstractions | Highest | 1w | None |
| 19.2 Pipeline Migration | Highest | 1w | 19.1 |
| 19.3 HTTP/2 Hot-Path | High | 1w | 19.1 |
| 19.4 UniTask Module | Medium | 3-4d | 19.1, 19.2 |
| 19.5 Benchmarks | High | 3-4d | All above |

## Verification Plan

1. All existing tests pass without modification (backward compatibility).
2. Allocation benchmarks show measurable reduction in hot paths.
3. HTTP/2 stream throughput does not regress under concurrency stress.
4. UniTask module compiles, works, and is fully optional (Core builds without it).
5. IL2CPP AOT validation on iOS and Android.

## Notes

- Task 19.2 and 19.3 can proceed in parallel after 19.1 is complete.
- The UniTask module should only be built when the `com.cysharp.unitask` package is present in the project — use `versionDefines` in the `.asmdef`.
- Profile actual allocations before committing to specific optimizations; avoid premature optimization of paths that aren't hot.
