# Phase 18a Implementation Plan - Overview

Phase 18a is split into 7 sub-phases. Each can be implemented and shipped independently after Phase 18, so teams can prioritize based on their use cases. The extension framework and compression (18a.1) should be prioritized first, followed by test suite (18a.7) as the final gate.

**Milestone:** M4 (v1.2)
**Dependencies:** Phase 18 (all sub-phases — core WebSocket fully implemented)
**Estimated Complexity:** High
**Estimated Effort:** 4-6 weeks
**Critical:** No — enhancement to Phase 18 WebSocket client

## Why These Enhancements?

Phase 18 was intentionally scoped to the RFC 6455 baseline. The following features were explicitly deferred or were identified as gaps during the Phase 18 review and implementation:

| Enhancement | Why It Was Deferred | Why It Matters Now |
|---|---|---|
| **Extension framework + `permessage-deflate`** | Phase 18 overview §19: "deferred to a future phase" | Only standardized WS extension; most servers (nginx, Cloudflare, AWS ALB) negotiate it by default. 60-90% bandwidth savings on text payloads. |
| **`IAsyncEnumerable` streaming receive** | Phase 18.4: method name `ReceiveAllAsync` was *reserved* but not implemented | Enables `await foreach` pattern — the idiomatic C# way to consume message streams. Reduces boilerplate significantly. |
| **Connection metrics & observability** | No coverage in Phase 18 | Production debugging requires bytes sent/received, message counts, compression ratios, latency histograms. Zero-allocation counter pattern. |
| **HTTP proxy tunneling** | No coverage in Phase 18 | Enterprise and mobile networks frequently use HTTP proxies. Without CONNECT tunnel support, WebSocket connections fail behind proxies. |
| **Typed message serialization** | Phase 18 overview §19: "sub-protocol dispatch is out of scope" | JSON-RPC, game state protocols, and event systems need strongly-typed send/receive. Reduces boilerplate and prevents serialization bugs. |
| **Connection health & diagnostics** | Phase 18.3 has basic keep-alive | Latency probing, bandwidth estimation, connection quality scoring for adaptive game networking. |

## Pre-Implementation Spikes

> [!CAUTION]
> These spikes **must** be completed and passed before the corresponding sub-phase begins implementation.

### Spike 1: `DeflateStream.Flush()` Behavior (gates 18a.1)

RFC 7692 §7.2.1 requires stripping the trailing `0x00 0x00 0xFF 0xFF` produced by `Z_SYNC_FLUSH`. On Unity Mono, `DeflateStream.Flush()` may produce `Z_FINISH` instead, and `FlushMode` is not exposed in .NET Standard 2.1.

**Test:** Create a minimal Unity project that compresses a short payload via `DeflateStream`, calls `Flush()` (not `Close()`), and checks whether the output ends with `0x00 0x00 0xFF 0xFF`. Run on:
- Unity Editor (Mono)
- iOS IL2CPP build
- Android IL2CPP build

**If fails:** Evaluate native zlib P/Invoke with explicit `Z_SYNC_FLUSH` control as fallback.

### Spike 2: `DeflateStream` Memory Usage (gates 18a.1)

The actual memory per `DeflateStream` instance varies significantly by runtime. zlib deflate state is ~256KB for compression at default level, ~44KB for decompression — **300–600KB total per connection with context takeover**, not 64KB.

**Test:** Measure `GC.GetTotalMemory(true)` before/after creating 10 `DeflateStream` pairs (compress + decompress). Run on Unity Mono and IL2CPP. Document per-pair memory cost.

### Spike 3: `IAsyncEnumerable` IL2CPP Compatibility (gates 18a.2)

`IAsyncEnumerable<T>` is natively included in .NET Standard 2.1 (no NuGet dependency required). However, IL2CPP code stripping may affect async enumerable state machine infrastructure.

**Test:** Create a minimal `async IAsyncEnumerable<int>` method with `yield return`, consume it via `await foreach`, and build for iOS/Android IL2CPP in Unity 2021.3 LTS. Verify runtime execution. If stripping occurs, add `link.xml` entries and re-test.

## Sub-Phase Index

| Sub-Phase | Name | New Files | Modified Files | Depends On |
|---|---|---|---|---|
| [18a.1](phase-18a.1-extension-framework.md) | Extension Framework & `permessage-deflate` | 6 | 4 | Phase 18, Spikes 1 & 2 |
| [18a.2](phase-18a.2-async-enumerable-receive.md) | `IAsyncEnumerable` Streaming Receive | 1 | 3 | Phase 18, Spike 3 |
| [18a.3](phase-18a.3-connection-metrics.md) | Connection Metrics & Observability | 2 | 3 | Phase 18 |
| [18a.4](phase-18a.4-proxy-tunneling.md) | HTTP Proxy Tunneling | 2 | 2 | Phase 18 |
| [18a.5](phase-18a.5-typed-serialization.md) | Typed Message Serialization | 3 | 1 | Phase 18 |
| [18a.6](phase-18a.6-connection-health.md) | Connection Health & Diagnostics | 1 | 2 | Phase 18, 18a.3 |
| [18a.7](phase-18a.7-test-suite.md) | Test Suite | 6 | 1 | All above |

## Dependency Graph

```text
Phase 18 (done — core WebSocket)
  ├── 18a.1 Extension Framework & permessage-deflate  [gated by Spikes 1 & 2]
  ├── 18a.2 IAsyncEnumerable Streaming Receive        [gated by Spike 3]
  ├── 18a.3 Connection Metrics & Observability
  │    └── 18a.6 Connection Health & Diagnostics
  ├── 18a.4 HTTP Proxy Tunneling
  ├── 18a.5 Typed Message Serialization
  │
  18a.1-18a.6
      └── 18a.7 Test Suite
```

> [!NOTE]
> Sub-phases 18a.1 through 18a.5 are independent of each other and can be implemented in parallel or in any order. 18a.6 depends on 18a.3 (uses the metrics infrastructure). 18a.7 covers testing for all sub-phases.

## Memory Limits (Defaults)

| Setting | Default | Notes |
|---------|---------|-------|
| `CompressionThreshold` | 128 bytes | Messages below this size skip compression |
| `ClientMaxWindowBits` | 15 | LZ77 window (does not matter in v1 `no_context_takeover` mode) |
| `CompressionLevel` | 6 | Balanced speed/ratio (maps to `CompressionLevel.Optimal`) |
| **Per-connection overhead (v1)** | ~0 | `no_context_takeover`: contexts are create-use-dispose per message |
| **Per-connection overhead (future context takeover)** | ~300-600KB | Two zlib contexts (deflate ~256KB, inflate ~44KB). Deferred. |

## All Files Summary

| Sub-Phase | New Files | Modified Files | Total |
|---|---|---|---|
| 18a.1 Extension Framework & Compression | 6 | 4 | 10 |
| 18a.2 IAsyncEnumerable Receive | 1 | 3 | 4 |
| 18a.3 Connection Metrics | 2 | 3 | 5 |
| 18a.4 HTTP Proxy Tunneling | 2 | 2 | 4 |
| 18a.5 Typed Serialization | 3 | 1 | 4 |
| 18a.6 Connection Health | 1 | 2 | 3 |
| 18a.7 Test Suite | 6 | 1 | 7 |
| **Total** | **21** | **16** | **37** |

## Prioritization Matrix

| Sub-Phase | Priority | Effort | Rationale |
|---|---|---|---|
| 18a.1 Compression | **Highest** | 2w | Bandwidth savings, server compatibility. Only standardized WS extension. |
| 18a.2 IAsyncEnumerable | High | 2-3d | Idiomatic C# API, small effort, big developer experience win. |
| 18a.3 Metrics | High | 3-4d | Production requirement for any non-trivial deployment. |
| 18a.4 Proxy | Medium | 3-4d | Enterprise/mobile blocker but not universal. |
| 18a.5 Serialization | Medium | 3-4d | Convenience — apps can do this themselves, but boilerplate reduction is valuable. |
| 18a.6 Health Monitor | Medium-Low | 2-3d | Game-specific feature. Depends on 18a.3. |

## Verification Plan

1. All Phase 18 tests still pass (no regressions).
2. **Pre-implementation spikes pass** (DeflateStream flush, memory, IAsyncEnumerable IL2CPP).
3. Extension negotiation builds correct headers and rejects invalid server responses.
4. `permessage-deflate` compress → decompress round-trip equals original for text and binary.
5. Chunk-based decompression detects zip bombs before full allocation.
6. RSV bits propagate correctly: reader → frame → assembler (first fragment) → extension transform.
7. Continuation frames with RSV1 set are rejected (RFC 7692 §6.1).
8. `await foreach` streaming receive consumes and closes cleanly.
9. Metrics counters are accurate under concurrent send/receive load, including on 32-bit IL2CPP.
10. HTTP CONNECT tunnel works through mock proxy with and without Basic authentication.
11. Typed JSON serialization round-trips complex objects correctly.
12. Health monitor detects quality degradation under injected latency via event-driven RTT.
13. IL2CPP validation: `DeflateStream`, async enumerable, `ProxyCredentials` all work on iOS/Android.
14. Compression + fragmentation: large compressed message fragments correctly with RSV1 on first fragment only.
15. `IMemoryOwner<byte>` ownership semantics verified — no leaks, correct memory lengths.

## Future Considerations (Out of Scope for 18a)

1. **Full context takeover** — pending Spike 1 & 2 results. If `DeflateStream.Flush()` produces correct `Z_SYNC_FLUSH` on Unity Mono, implement in a follow-up. Otherwise, evaluate native zlib P/Invoke.
2. **HTTP/2 WebSocket (RFC 8441)** — CONNECT method with `:protocol` pseudo-header. Requires deep HTTP/2 stream multiplexing changes. Recommend separate Phase 18b.
3. **WebSocket over HTTP/3 (RFC 9220)** — depends on QUIC transport (not yet in TurboHTTP).
4. **Native zlib bindings** — for better compression performance on Mono/Unity. Evaluate after benchmarking.
5. **WebSocket multiplexing** — multiple logical channels over a single WebSocket (draft spec, not standardized).
6. **Custom extension SDK documentation** — Phase 18a delivers the framework; user-facing extension authoring guide is future work.
7. **HTTPS proxy support** — CONNECT through HTTPS proxy endpoint. Deferred from 18a.4.
8. **System proxy detection** — `UseSystemProxy` with platform-specific implementations. Deferred from 18a.4.
9. **Digest proxy authentication** — complex nonce handling. Deferred; Basic auth only for v1.
