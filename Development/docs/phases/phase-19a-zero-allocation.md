# Phase 19a: Extreme Performance & Zero-Allocation Networking (Greenfield)

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 19 (Async Runtime Refactor)
**Estimated Complexity:** Very High
**Estimated Effort:** 6-8 weeks
**Critical:** No — performance optimization track for high-throughput and high-tick-rate workloads.

## Overview

Phase 19 reduced async scheduling overhead (`ValueTask` migration). Phase 19a focuses on memory discipline and allocation elimination in transport/serialization hot paths.

This document is the phase-level summary. The authoritative implementation plan lives in:
- `Development/docs/phases/phase19a/overview.md`
- `Development/docs/phases/phase19a/phase-19a.0-safety-infrastructure.md`
- `Development/docs/phases/phase19a/phase-19a.1-arraypool-completion.md`
- `Development/docs/phases/phase19a/phase-19a.2-ibufferwriter-serialization.md`
- `Development/docs/phases/phase19a/phase-19a.3-segmented-sequences.md`
- `Development/docs/phases/phase19a/phase-19a.4-saea-socket-wrapper.md`
- `Development/docs/phases/phase19a/phase-19a.5-http-object-pooling.md`
- `Development/docs/phases/phase19a/phase-19a.6-system-tls-priority.md`

## Greenfield Assumptions

1. This is a new library track with no user migration burden.
2. Zero-allocation runtime paths are primary architecture, not optional compatibility features.
3. Temporary rollback controls are allowed during alpha hardening, but are not long-term API commitments.
4. File/type references in this phase follow current repo layout (`UHttpClientOptions`, `TlsBackend`, `Runtime/JSON`, `Runtime/Files`, `Runtime/Transport/...`).

## High-Level Goals

| Area | Problem | Target Outcome |
|---|---|---|
| Buffer pooling completion | Remaining `new byte[]` in HTTP/2/WebSocket hot paths causes avoidable GC churn. | Replace with pooled ownership/lifetime discipline. |
| Serialization pipeline | JSON/multipart currently rely on intermediate strings/arrays and copies. | Buffer-writer-first serialization and leased request body ownership. |
| Segmented body handling | Growing contiguous buffers create copy amplification and LOH pressure. | Segmented sequences plus lazy flattening where contiguous bytes are required. |
| Internal object churn | Short-lived internal objects increase allocator pressure under sustained load. | Pool resettable internal objects with strict reset safety. |
| TLS provider behavior | Ambiguous fallback rules can become insecure when tied to auth failures. | System TLS first, capability-only fallback, fail closed on cert/auth failures. |
| Socket async overhead | `NetworkStream` async paths can allocate more on some runtimes. | Add opt-in SAEA mode for measurable performance gains. |

## Sub-Phase Index

| Sub-Phase | Name | Depends On | Estimated Effort |
|---|---|---|---|
| 19a.0 | Runtime Safety Infrastructure & Diagnostics | None | 2-3 days |
| 19a.1 | `ArrayPool<byte>` Completion Sweep | Phase 19 | 2-3 days |
| 19a.2 | Buffer-Writer-First Serialization | 19a.0, 19a.1 | 1 week |
| 19a.3 | Segmented Sequences (`ReadOnlySequence<byte>`) | 19a.1 | 1 week |
| 19a.4 | SAEA Socket I/O Mode (Opt-In) | Phase 19 Tasks 19.1 + 19.2 | 2 weeks |
| 19a.5 | Internal HTTP Object Pooling | 19a.0, 19a.1 | 3-4 days |
| 19a.6 | TLS Provider Hardening (System-first, Safe Fallback) | 19a.1 | 1 week |

---

## 19a.0: Runtime Safety Infrastructure & Diagnostics

**Goal:** Add safety rails and instrumentation needed for aggressive pooling work.

Summary:
1. Add `UHttpClientOptions.EnableZeroAllocPipeline` (default `true`, temporary rollback control).
2. Add debug-only pooled-buffer guards for use-after-return and double-return detection.
3. Add diagnostics to `ByteArrayPool` and `ObjectPool<T>` plus periodic health reporting.

---

## 19a.1: `ArrayPool<byte>` Completion Sweep

**Goal:** Remove remaining hot-path `new byte[]` and complete shared pooled ownership primitives.

Summary:
1. Promote `ArrayPoolMemoryOwner<T>` to `TurboHTTP.Performance`.
2. Replace remaining HTTP/2 and WebSocket hot-path array allocations with pooled rents.
3. Refactor transport encoding helpers away from transient byte-array allocation patterns.
4. Prepare multipart internals for writer-based output in 19a.2.

---

## 19a.2: Buffer-Writer-First Serialization

**Goal:** Move request serialization to pooled writable buffers with explicit ownership.

Summary:
1. Extend `IJsonSerializer` with `IBufferWriter<byte>`/`ReadOnlySequence<byte>` methods.
2. Add `PooledArrayBufferWriter` with explicit ownership transfer (`DetachAsOwner` pattern).
3. Add leased request-body ownership in `UHttpRequest`/builder/transport pipeline.
4. Move JSON and multipart request generation to buffer-writer-first flow.

---

## 19a.3: Segmented Sequences (`ReadOnlySequence<byte>`)

**Goal:** Prevent contiguous growth/copy patterns in large payload and fragmentation flows.

Summary:
1. Add pooled segment chain primitives (`PooledSegment`, `SegmentedBuffer`).
2. Add `SegmentedReadStream` for stream-based consumers over segmented input.
3. Integrate segmented assembly into HTTP chunked/decompression paths.
4. Integrate segmented assembly into WebSocket fragment assembly.

---

## 19a.4: SAEA Socket I/O Mode (Opt-In)

**Goal:** Provide an optional SAEA-based socket I/O mode for targeted performance gains.

Summary:
1. Add `ConnectionPoolOptions.SocketIoMode` (`NetworkStream` default, `Saea` opt-in).
2. Implement reusable SAEA completion source and channel abstractions.
3. Integrate mode selection in `TcpConnectionPool` without changing public transport APIs.
4. Keep TLS integration through `ITlsProvider` and benchmark gains with/without TLS.

Scope notes:
1. Transport assemblies currently exclude WebGL; this phase does not implement WebGL fallback transport behavior.
2. `NetworkStream` mode remains baseline/default.

---

## 19a.5: Internal HTTP Object Pooling

**Goal:** Pool short-lived internal objects while preserving external lifetime safety.

Summary:
1. Do not pool `UHttpResponse` (user-visible lifetime object).
2. Pool internal `ParsedResponse` and `Http2Stream` state objects.
3. Pool header parsing scratch objects.
4. Pool HPACK encoder state wrappers/scratch structures.

---

## 19a.6: TLS Provider Hardening (System-first, Safe Fallback)

**Goal:** Improve TLS provider policy and diagnostics without unsafe downgrade behavior.

Summary:
1. Keep `TlsBackend` as public selector (`Auto`, `SslStream`, `BouncyCastle`).
2. In `Auto`, fallback is capability-based only.
3. Never fallback after certificate validation or authentication failure.
4. Add platform capability diagnostics for TLS provider behavior.

---

## Verification

### Performance
- Profile sustained throughput (target benchmark scenario: 10k req/s synthetic load).
- Steady-state managed heap growth target after warmup: `< 1 KB/sec` on hot paths.
- Track per-request allocations before/after each sub-phase.

### Correctness
- Existing test suite passes with `EnableZeroAllocPipeline = true`.
- Temporary rollback mode (`EnableZeroAllocPipeline = false`) remains functional during alpha.
- No regression in protocol correctness (HTTP/1.1, HTTP/2, WebSocket, TLS).

### Safety
- Debug pooled-buffer guard catches use-after-return and double-return defects.
- No cross-request data leakage through pooled buffers or pooled objects.
- TLS tests confirm fail-closed behavior on cert/auth failures (no fallback downgrade).

### Platform Validation
- Desktop: Windows, macOS, Linux.
- Mobile: iOS/Android IL2CPP validation.
- WebGL: out of scope for Transport assembly work under current asmdef strategy.
