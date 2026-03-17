# Phase 22a: End-to-End Request/Response Streaming — Overview

**Milestone:** M4 (v2.0 follow-up)
**Dependencies:** Phase 19a-19c, Phase 22.1-22.4
**Estimated Complexity:** Very High
**Critical:** Yes — this is the phase that removes the remaining buffered-body constraints from the client/transport stack.
**Compatibility:** Clean break. No backward compatibility is required.

> **Source document:** The detailed plan lives at `Development/docs/phases/phase-22a-end-to-end-streaming.md`. This directory breaks the plan into per-sub-phase implementation files.

## Context

Phase 22 introduced the interceptor boundary and removed the old buffered transport bridge, but the body model is still mixed:

1. request bodies are still fundamentally in-memory payloads
2. HTTP/1.1 response parsing still buffers the full body before the public API sees it
3. `ResponseCollectorHandler` remains the default public API path
4. decompression, file download, cache store, and monitor capture still have buffered assumptions
5. the current synchronous `IHttpHandler.OnResponseData(...)` callback model has no natural backpressure contract

Phase 22a is the clean-break streaming phase. It makes both upload and download streaming first-class, while preserving a separate optimized buffered fast path for small payloads and explicitly bounded memory behavior for large payloads.

## Core Design Principles

1. **Dual Path, Not One Path** — buffered mode (small known-length payloads) and streaming mode (large/unknown-length payloads) as explicit first-class paths.
2. **Pull-Based Body Consumption** — `IResponseBodySource.ReadAsync` replaces push callbacks (`OnResponseData`). Natural backpressure.
3. **Replayability Is Explicit** — request bodies declare `RequestBodyReplayability` for correct retry/redirect behavior.
4. **Bounded Memory Beats "Best Effort"** — explicit buffer sizes, no unbounded growth on large payloads.
5. **Buffered Large Payloads Still Avoid Extra Copies** — transport must not add a second full-body copy.

## Sub-Phase Index

| Sub-Phase | Name | Effort | Depends On |
|-----------|------|--------|------------|
| [22a.1](phase-22a.1-core-body-model.md) | Core Body Model and Public API Split | 3-4 days | Phase 22.4 |
| [22a.2](phase-22a.2-http11-streaming.md) | HTTP/1.1 Streaming Send/Receive | 4-5 days | 22a.1 |
| [22a.3](phase-22a.3-http2-streaming.md) | HTTP/2 Streaming Send/Receive | 4-5 days | 22a.1 |
| [22a.4](phase-22a.4-buffered-fast-path.md) | Buffered Fast Path and Performance Tuning | 3-4 days | 22a.2, 22a.3 |
| [22a.5](phase-22a.5-interceptor-rewrite.md) | Interceptor and Module Streaming Rewrite | 4-6 days | 22a.4 |
| [22a.6](phase-22a.6-validation.md) | Validation, Benchmarks, Mobile/IL2CPP Pass | 3-5 days | 22a.5 |

## Dependency Graph

```
Phase 22.4 (done)
    │
    22a.0 IL2CPP Spike (IAsyncDisposable + ValueTask<T>) [BLOCKING]
    │
    22a.1 Core Body Model + Public API Split
    ├── 22a.2 HTTP/1.1 Streaming Send/Receive
    ├── 22a.3 HTTP/2 Streaming Send/Receive
    └──────────┬───────────────
               22a.4 Buffered Fast Path + Performance Tuning
               │
               22a.5 Interceptor + Module Streaming Rewrite
               │
               22a.6 Validation, Benchmarks, Mobile/IL2CPP Pass
```

Sub-phases 22a.2 and 22a.3 can be implemented in parallel (no inter-dependencies). 22a.4 depends on both transport sub-phases. 22a.5 depends on the finalized buffered fast-path contract from 22a.4.

## Public API Shape (Clean Break)

```csharp
// Old (removed):
Task<UHttpResponse> SendAsync(UHttpRequest request, CancellationToken ct = default);

// New:
Task<UHttpResponse> SendBufferedAsync(UHttpRequest request, CancellationToken ct = default);
Task<UHttpStreamingResponse> SendStreamingAsync(UHttpRequest request, CancellationToken ct = default);
```

`UHttpRequest.Body` removed, replaced with `Content: UHttpRequestBody`. See [22a.1](phase-22a.1-core-body-model.md) for full API details.

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| `SendAsync` removed, replaced with `SendBufferedAsync`/`SendStreamingAsync` | Eliminates ambiguity; clean break as stated in Compatibility |
| `IResponseBodySource` is public in Core; `Fault` is NOT on public interface | Interface for Transport + module wrappers. `Fault` is transport-internal via `IFaultableResponseBodySource` |
| `IResponseBodySource.TryGetBufferedData` included from day one | Interface shape frozen in 22a.1 to avoid breaking implementations in later sub-phases |
| `OnResponseData`/`OnResponseEnd` removed from `IHttpHandler` | Replaced by `OnResponseStartAsync(..., IResponseBodySource, ...)` — single place to swap/wrap body source |
| `DispatchBridge` split into `BufferedDispatchBridge` + `StreamingDispatchBridge` | Different completion semantics: buffered completes after full drain; streaming completes after headers only. `StreamingDispatchBridge` has `try/finally` lease safety. |
| HTTP/2 decoupled WINDOW_UPDATE model | Connection-level with threshold batching (prevents cross-stream starvation), per-stream on consumption (true backpressure). Aggregate bounded by `MaxConnectionBufferedBytes`. |
| `SingleReaderChannel<T>` in Transport (not Core) | Unity 2021.3 lacks `System.Threading.Channels`; purpose-built SPSC channel. Lives in Transport because it's solely an HTTP/2 concern. |
| `ReadAsync` cancellation contract is implementation-dependent | HTTP/2: non-destructive (queue-based). HTTP/1.1: transitions to faulted state (connection not reusable). |
| Request timeout applies to headers only for streaming | Body reads governed by consumer's `CancellationToken`; large file downloads don't hit request timeout |
| `FileRequestBody` in `TurboHTTP.Files`, not Core | Core stays file-I/O-free; follows `WithJsonBody` → JSON, `WithBearerToken` → Auth pattern |
| GZIP trailer validation: rely on `GZipStream` internal CRC32 | Streaming mode cannot buffer full compressed sequence; early dispose skips validation (documented) |
| `ResponseBodyStream` has no read-ahead buffer | Thin adapter only. Read-ahead (~8KB) lives in `DecompressionBodySource` where it's needed. |
| `StreamRequestBody` captures `_startPosition` at construction | Replay seeks to captured position, not to 0. Prevents incorrect replay for partial uploads. |
| Content-Length/Transfer-Encoding enforced by serializer | Transport-set headers override user-set values. RFC 9110 Section 8.6 compliance. |
| Response framing: TE checked before CL | Per RFC 9112 Section 6.1, `Transfer-Encoding` overrides `Content-Length` when both present. |

## Non-Goals

1. WebGL transport redesign
2. Brotli support
3. OS-specific zero-copy syscalls (`sendfile`)
4. HTTP/3/QUIC or gRPC
5. Full backward compatibility with current buffered-only API
6. `Expect: 100-continue` handling (deferred to post-22a)
7. Streaming through proxy connections (deferred to post-22a)
8. HTTP/1.1 trailer parsing (`GetTrailersAsync` returns `HttpHeaders.Empty`)
9. HTTP/1.1 request trailers

## Performance and Memory Targets

| Setting | Initial Target | Notes |
|--------|----------------|-------|
| `SmallBufferedRequestThresholdBytes` | 32 KB | |
| `DefaultStreamingSendBufferBytes` | 32 KB | |
| `DefaultStreamingReceiveBufferBytes` (HTTP/1.1) | 64 KB | |
| `DefaultHttp2PerStreamReceiveBufferBytes` | 256 KB | Must be >= advertised initial window size |
| `BufferedDrainReuseThresholdBytes` (HTTP/1.1) | 64 KB | Remaining unread bytes; applies to both Content-Length and chunked drain |
| `MaxConnectionBufferedBytes` (HTTP/2) | 8 MB | Aggregate cap across all concurrent streams |
| `Http2StallTimeoutSeconds` | 60s | Coarse-grained check in read loop, not per-stream Timer |

## Risks

See the full plan document (`phase-22a-end-to-end-streaming.md`) for the complete risk register covering design, platform, protocol, and implementation risks.

## Review Model

Both specialist agent reviews are mandatory per sub-phase:
- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`

22a.6 is the final integration review with full transport benchmarks and IL2CPP validation on physical devices.
