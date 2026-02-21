# Phase 19: Async Runtime Refactor with Optional UniTask Adapters

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 4 (Pipeline), Phase 3B (HTTP/2), Phase 6 (Performance), Phase 15 (Runtime Hardening)
**Estimated Complexity:** High
**Estimated Effort:** 3-4 weeks
**Critical:** No - Performance optimization

## Overview

Reduce async overhead in hot paths (allocation and scheduling churn) by migrating the entire internal and public API surface to `ValueTask`-first patterns. **This migration is done primarily to enable zero-overhead optional UniTask integration** — UniTask natively converts from `ValueTask` without allocation, but converting from `Task` requires wrapping and defeats the purpose. By making `ValueTask` the core currency, the optional `TurboHTTP.UniTask` adapter module becomes a thin, zero-allocation bridge rather than a costly conversion layer.

Since TurboHTTP is a new package with no released public API contract, all interfaces (`IHttpMiddleware`, `IHttpTransport`, `HttpPipelineDelegate`, `UHttpClient.SendAsync`) are migrated directly — no backward compatibility shims or dual-interface periods are needed. Core has zero dependency on UniTask; the adapter module is optional.

This is a cross-cutting architectural refactor — similar in nature to Phase 6 (Performance) — not a feature addition. (Note: For absolute zero-allocation strategies including buffer pooling and non-blocking SAEA socket wrappers targeting extreme concurrency, see **Phase 19a: Extreme Performance & Zero-Allocation Networking**).

## Design Constraints

- `TurboHTTP.Core` must **not** reference `com.cysharp.unitask`.
- UniTask support is provided as an optional adapter module in a separate assembly — the `ValueTask`-first pipeline is the prerequisite that makes this adapter zero-overhead.

## Tasks

### Task 19.1: ValueTask Migration

**Goal:** Migrate the entire pipeline to `ValueTask`-first execution paths — interfaces, delegates, middleware, transport, and public API.

**Current State:**
- `IHttpInterceptor` and `IHttpPlugin` already use `ValueTask` — these are already aligned.
- `AdaptiveMiddleware` currently bridges `ValueTask` (interceptors/plugins) → `Task` (pipeline) — this bridge can be **removed** after migration.
- 15+ middleware implementations (`Auth/`, `Cache/`, `Middleware/`, `Observability/`, `Performance/`, `Retry/`) currently return `Task<UHttpResponse>` — all must be migrated.

**Deliverables:**
- Migrate `HttpPipelineDelegate` from `Task<UHttpResponse>` → `ValueTask<UHttpResponse>`
- Migrate `IHttpMiddleware.InvokeAsync` return type to `ValueTask<UHttpResponse>`
- Migrate `IHttpTransport.SendAsync` return type to `ValueTask<UHttpResponse>`
- Migrate `HttpPipeline.ExecuteAsync` and `UHttpClient.SendAsync` to return `ValueTask<UHttpResponse>`
- Update all 15+ middleware implementations to return `ValueTask<UHttpResponse>`
- Remove the `ValueTask` → `Task` bridge in `AdaptiveMiddleware`

**Estimated Effort:** 1 week

---

### Task 19.2: Pipeline & Transport Migration

**Goal:** Migrate the request pipeline dispatch path, transport layer, and connection pools to fully utilize `ValueTask` for synchronous fast paths.

**Priority Targets (highest ROI):**
- `Http2ConnectionManager.GetOrCreateAsync` — has a synchronous fast path (cached alive connection) that completes synchronously ~90% of the time; `ValueTask` eliminates `Task` allocation on warm hosts
- `TcpConnectionPool.GetConnectionAsync` — has a synchronous idle-connection reuse fast path

**Deliverables:**
- Middleware hop chain uses `ValueTask` natively (no bridge needed after 19.1)
- Transport send/receive paths return `ValueTask`
- `Http2ConnectionManager.GetOrCreateAsync` returns `ValueTask<Http2Connection>`
- `TcpConnectionPool.GetConnectionAsync` returns `ValueTask<ConnectionLease>`
- All internal transport paths use `ValueTask`

**Estimated Effort:** 1 week

---

### Task 19.3: HTTP/2 Hot-Path Refactor

**Goal:** Eliminate `TaskCompletionSource` per-operation overhead in HTTP/2 stream lifecycle using poolable `ValueTask` sources.

**Target API:** `ManualResetValueTaskSourceCore<T>` backing `IValueTaskSource<T>` implementations. These allow creating reusable, poolable `ValueTask`-backing objects that avoid per-operation `TaskCompletionSource` allocations.

**IL2CPP Pre-Requisite:** `ManualResetValueTaskSourceCore<T>` is a mutable struct with complex generic instantiation. Before implementing, validate with an IL2CPP AOT smoke test (build + run a minimal `IValueTaskSource<bool>` on iOS IL2CPP). **Fallback strategy:** if IL2CPP issues arise, pool `TaskCompletionSource<T>` objects via `ObjectPool<T>` (less optimal but safe).

**Deliverables:**
- `Http2Stream.ResponseTcs` replaced with pooled `IValueTaskSource<UHttpResponse>` implementation
- `Http2Connection._settingsAckTcs` replaced with pooled source
- Request pipeline dispatch path (middleware hop overhead) optimized
- Other frequently-called internal async helpers identified by profiling

**Scope note:** `HappyEyeballsConnector.cs` `TaskCompletionSource` allocations (cancel signals, `Task.WhenAny`) occur **once per connection establishment**, not per request. Optimization of Happy Eyeballs is low-priority and can be deferred to Phase 19a if needed.

**Estimated Effort:** 1 week

---

### Task 19.4: Optional UniTask Adapter Module

**Goal:** Provide UniTask-based API surface as a separate, optional assembly. Core must have zero dependency on UniTask.

**Deliverables:**
- Separate `TurboHTTP.UniTask` assembly (gated by asmdef `versionDefines`)
- Extension methods: `GetAsync().AsUniTask()`, `SendAsync().AsUniTask()`
- Adapters map to the new `ValueTask` fast paths directly (no extra wrapping needed since Core is already `ValueTask`-first)
- `PlayerLoopTiming` integration for frame-aware scheduling — default `PlayerLoopTiming.Update`, configurable via `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming`
- Pass through existing request `CancellationToken` to UniTask continuations (do not create new `CancellationTokenSource` instances)

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
| 19.1 ValueTask Migration | Highest | 1w | None |
| 19.2 Pipeline & Transport Migration | Highest | 1w | 19.1 |
| 19.3 HTTP/2 Hot-Path Refactor | High | 1w | 19.1 |
| 19.4 Optional UniTask Adapter Module | Medium | 3-4d | 19.1, 19.2 |
| 19.5 Benchmarks & Regression Validation | High | 3-4d | All above |

## Verification Plan

1. All existing tests pass after `ValueTask` migration.
2. Allocation benchmarks show measurable reduction in hot paths.
3. HTTP/2 stream throughput does not regress under concurrency stress.
4. UniTask module compiles, works, and is fully optional (Core builds without it).
5. IL2CPP AOT validation on iOS and Android (including `IValueTaskSource<T>` smoke test).
6. `ValueTask` single-consumption guarantee validated under concurrent middleware + HTTP/2 multiplexed request stress test — verifies no `ValueTask` instance is consumed (awaited) more than once.

## Notes

- Task 19.2 and 19.3 can proceed in parallel after 19.1 is complete.
- The UniTask module should only be built when the `com.cysharp.unitask` package is present in the project — use `versionDefines` in the `.asmdef`.
- Profile actual allocations before committing to specific optimizations; avoid premature optimization of paths that aren't hot.
- `ValueTask` instances must never be awaited more than once or stored for later use. This is the #1 footgun of `ValueTask` migration — enforce via code review and stress tests.
