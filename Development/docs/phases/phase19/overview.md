# Phase 19 Implementation Plan - Overview

Phase 19 is split into 5 sub-phases. The ValueTask migration is the foundation — all interfaces, delegates, middleware, transport, and public API surface are migrated to `ValueTask`-first patterns. Pipeline & transport and HTTP/2 hot-path refactors build on top in parallel. The UniTask adapter module depends on pipeline completion, and the benchmark/validation suite is the final gate.

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 4 (Pipeline), Phase 3B (HTTP/2), Phase 6 (Performance), Phase 15 (Runtime Hardening)
**Estimated Complexity:** High
**Estimated Effort:** 3-4 weeks
**Critical:** No - Performance optimization

## Purpose

Reduce async overhead in hot paths (allocation and scheduling churn) by migrating the entire internal and public API surface to `ValueTask`-first patterns. **This migration is done primarily to enable zero-overhead optional UniTask integration** — UniTask natively converts from `ValueTask` without allocation, but converting from `Task` requires wrapping and defeats the purpose. By making `ValueTask` the core currency, the optional `TurboHTTP.UniTask` adapter module becomes a thin, zero-allocation bridge rather than a costly conversion layer.

Since TurboHTTP is a new package with no released public API contract, all interfaces (`IHttpMiddleware`, `IHttpTransport`, `HttpPipelineDelegate`, `UHttpClient.SendAsync`) are migrated directly — no backward compatibility shims or dual-interface periods are needed. Core has zero dependency on UniTask; the adapter module is optional.

This is a cross-cutting architectural refactor — similar in nature to Phase 6 (Performance) — not a feature addition. (Note: For absolute zero-allocation strategies including buffer pooling and non-blocking SAEA socket wrappers targeting extreme concurrency, see **Phase 19a: Extreme Performance & Zero-Allocation Networking**).

## Sub-Phase Index

| Sub-Phase | Name | Depends On |
|---|---|---|
| [19.1](phase-19.1-valuetask-migration.md) | ValueTask Migration | None |
| [19.2](phase-19.2-pipeline-transport-migration.md) | Pipeline & Transport Migration | 19.1 |
| [19.3](phase-19.3-http2-hot-path-refactor.md) | HTTP/2 Hot-Path Refactor | 19.1 |
| [19.4](phase-19.4-unitask-adapter-module.md) | Optional UniTask Adapter Module | 19.1, 19.2 |
| [19.5](phase-19.5-benchmarks-regression-validation.md) | Benchmarks & Regression Validation | All above |

## Dependency Graph

```text
Phase 4 (done — Pipeline)
Phase 3B (done — HTTP/2)
Phase 6 (done — Performance)
Phase 15 (done — Runtime Hardening)
    │
    └── 19.1 ValueTask Migration
         ├── 19.2 Pipeline & Transport Migration
         │    └── 19.4 Optional UniTask Adapter Module
         ├── 19.3 HTTP/2 Hot-Path Refactor
         │
         19.1-19.4
             └── 19.5 Benchmarks & Regression Validation
```

19.2 and 19.3 can proceed in parallel after 19.1 is complete. 19.4 depends on 19.1 and 19.2.

## Design Constraints

- `TurboHTTP.Core` must **not** reference `com.cysharp.unitask`.
- UniTask support is provided as an optional adapter module in a separate assembly — the `ValueTask`-first pipeline is the prerequisite that makes this adapter zero-overhead.
- `ValueTask` instances must never be awaited more than once or stored for later use. This is the #1 footgun of `ValueTask` migration — enforce via code review and stress tests.

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
