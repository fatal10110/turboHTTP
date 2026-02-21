# Phase 19a Implementation Plan - Overview

Phase 19a is split into 7 sub-phases. Safety infrastructure and buffer pooling ship first, then serialization and segmented sequences build on top. SAEA socket wrapper and object pooling can proceed in parallel once Phase 19 foundations are in place. System TLS priority finalizes the performance picture.

## Sub-Phase Index

| Sub-Phase | Name | Depends On | Estimated Effort |
|---|---|---|---|
| [19a.0](phase-19a.0-safety-infrastructure.md) | Safety Infrastructure & Debug Guards | None | 2-3 days |
| [19a.1](phase-19a.1-arraypool-completion.md) | `ArrayPool<byte>` Completion Sweep | Phase 19 | 1-2 days |
| [19a.2](phase-19a.2-ibufferwriter-serialization.md) | `IBufferWriter<byte>` Serialization Paths (Additive) | 19a.1 | 1 week |
| [19a.3](phase-19a.3-segmented-sequences.md) | Segmented Sequences (`ReadOnlySequence`) | 19a.1 | 1 week |
| [19a.4](phase-19a.4-saea-socket-wrapper.md) | High-Performance SAEA Socket Wrapper (Opt-In) | Phase 19 Tasks 19.1 + 19.2 | 2 weeks |
| [19a.5](phase-19a.5-http-object-pooling.md) | HTTP Object Pooling | Phase 19 | 2-3 days |
| [19a.6](phase-19a.6-system-tls-priority.md) | System TLS Priority with BouncyCastle Fallback | 19a.1 | 1 week |

## Dependency Graph

```text
Phase 19 (done — Async Runtime Refactor, ValueTask migration)
    │
    ├── 19a.0 Safety Infrastructure & Debug Guards (no dependencies)
    │
    ├── 19a.1 ArrayPool<byte> Completion Sweep
    │    ├── 19a.2 IBufferWriter<byte> Serialization Paths
    │    ├── 19a.3 Segmented Sequences (ReadOnlySequence)
    │    └── 19a.6 System TLS Priority with BouncyCastle Fallback
    │
    ├── 19a.4 High-Performance SAEA Socket Wrapper (requires 19.1 + 19.2)
    │
    └── 19a.5 HTTP Object Pooling
```

19a.0 has no dependencies and should be implemented first. 19a.1 depends on Phase 19 and gates 19a.2, 19a.3, and 19a.6. 19a.4 and 19a.5 depend only on Phase 19 core tasks and can proceed in parallel with 19a.1.

## Why These Enhancements?

| Feature | The Problem | The Zero-Allocation Solution |
|---|---|---|
| **Buffer Pooling** | Remaining `new byte[]` allocations in HTTP/2 framing, HPACK encoding, and handshake validation cause Managed Heap pressure. | Complete remaining migration to `ArrayPool<byte>.Shared` via `IMemoryOwner<byte>` for all internal buffers, guaranteeing deterministic disposal. |
| **Direct Serialization** | Serializers write to intermediate arrays which are then copied into the socket's outbound buffer. | Expose `IBufferWriter<byte>` via new additive interfaces, allowing opt-in direct writes into pooled network buffers without breaking existing APIs. |
| **Segmented Decompression** | Growing contiguous buffers (e.g. `16KB -> 32KB -> 64KB`) causes Large Object Heap (LOH) exhaustion and GC spikes. | Decompress/read into a linked list of buffers (resembling `ReadOnlySequence<byte>`) bypassing chunk copying entirely. |
| **Object Pooling** | HTTP context and header dictionary allocations per request bloat memory under heavy connection load. | Aggressively pool `UHttpResponse`, `Http2Stream`, and header collections via the existing `ObjectPool<T>`. |
| **System TLS Priority** | BouncyCastle's internal buffer management creates massive GC pressure during symmetric encryption. | Prioritize `SslStream` (system TLS) on platforms where hardware-accelerated TLS 1.2/1.3 is available, falling back to BouncyCastle only where system TLS is unavailable. |
| **Non-Blocking Sockets** | Unity's `NetworkStream.ReadAsync` exhibits legacy boxing/APM overhead allocating a `Task` per packet payload on mono targets. | Implement opt-in zero-allocation Socket wrappers utilizing `SocketAsyncEventArgs` (SAEA) on supported platforms. |

## Cross-Cutting Design Decisions

1. All hot-path buffers must use `ArrayPool<byte>.Shared` via `IMemoryOwner<byte>` for deterministic disposal.
2. Data is passed using `Memory<byte>`, `ReadOnlyMemory<byte>`, or `Span<byte>` throughout the pipeline.
3. Cold-path allocations (Auth, Observability, Testing) are explicitly excluded from the zero-allocation sweep.
4. Existing `ObjectPool<T>` and `ByteArrayPool` infrastructure is reused — no new pooling primitives.
5. All new codepaths are gated behind `TurboHttpConfig.UseZeroAllocPipeline` for safe opt-in.
6. `PooledBuffer<T>` debug wrapper (enabled via `#if DEBUG`) detects use-after-return, double-return, and buffer overruns.
7. BouncyCastle source code is NOT modified — system TLS bypasses it entirely on supported platforms.
8. SAEA socket wrapper is opt-in; `NetworkStream` remains the default transport.

## Industry Reference

A major differentiator for libraries like BestHTTP is exactly the features proposed here. BestHTTP employs its own heavily tuned `BufferPool`, manages contiguous data via sliced array lists (avoiding LOH), and implements custom socket polling and platform-native TLS selection. Phase 19a essentially enforces these enterprise architecture decisions into TurboHTTP, while avoiding the maintenance burden of forking TLS internals.

## Verification Plan

### Benchmarking (Critical)
- Run a memory profiler (Unity Profiler / dotMemory) tracking a **10,000 requests-per-second** pipeline.
- **Steady-state metric** (after 1,000-request warmup): Managed heap expansion must be **< 1 KB/sec** of new allocations.
- Use `BenchmarkDotNet` with `MemoryDiagnoser` for micro-benchmarks of individual sub-phases.

### Integration
- Sequence readers correctly process fragments spanning buffer boundaries without data corruption.
- TLS provider selection correctly falls back to BouncyCastle on unsupported platforms.
- SAEA transport (opt-in) passes the full existing test suite on desktop platforms.

### Safety Validation
- `PooledBuffer<T>` debug mode detects all use-after-return and double-return bugs during test suite execution.
- All existing tests pass with `UseZeroAllocPipeline = true` and `UseZeroAllocPipeline = false`.
- No regression in API behavior, error mapping, or middleware ordering.

### Platform Matrix
- IL2CPP AOT validation on iOS and Android.
- `SslStream` TLS negotiation verified on Windows, macOS, Linux, iOS, Android.
- BouncyCastle fallback verified on at least one platform by forcing `TlsProviderMode.BouncyCastleOnly`.
