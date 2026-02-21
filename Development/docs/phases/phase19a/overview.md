# Phase 19a Implementation Plan - Overview (Greenfield Revision)

Phase 19a is split into 7 sub-phases. This revision assumes **no external users and no migration burden**. We optimize for the best long-term architecture now, not dual-path compatibility.

## Sub-Phase Index

| Sub-Phase | Name | Depends On | Estimated Effort |
|---|---|---|---|
| [19a.0](phase-19a.0-safety-infrastructure.md) | Runtime Safety Infrastructure & Diagnostics | None | 2-3 days |
| [19a.1](phase-19a.1-arraypool-completion.md) | `ArrayPool<byte>` Completion Sweep | Phase 19 | 2-3 days |
| [19a.2](phase-19a.2-ibufferwriter-serialization.md) | Buffer-Writer-First Serialization | 19a.0, 19a.1 | 1 week |
| [19a.3](phase-19a.3-segmented-sequences.md) | Segmented Sequences (`ReadOnlySequence<byte>`) | 19a.1 | 1 week |
| [19a.4](phase-19a.4-saea-socket-wrapper.md) | SAEA Socket I/O Mode (Opt-In) | Phase 19 Tasks 19.1 + 19.2 | 2 weeks |
| [19a.5](phase-19a.5-http-object-pooling.md) | Internal HTTP Object Pooling | 19a.0, 19a.1 | 3-4 days |
| [19a.6](phase-19a.6-system-tls-priority.md) | TLS Provider Hardening (System-first, Safe Fallback) | 19a.1 | 1 week |

## Dependency Graph

```text
Phase 19 (done: async runtime + ValueTask migration)
    │
    ├── 19a.0 Runtime Safety Infrastructure & Diagnostics
    │
    ├── 19a.1 ArrayPool<byte> Completion Sweep
    │    ├── 19a.2 Buffer-Writer-First Serialization
    │    ├── 19a.3 Segmented Sequences
    │    └── 19a.6 TLS Provider Hardening
    │
    ├── 19a.4 SAEA Socket I/O Mode (parallel track)
    │
    └── 19a.5 Internal HTTP Object Pooling
```

## Greenfield Decisions (Authoritative)

1. No migration scaffolding is required. Breaking internal contracts is allowed in this phase.
2. Zero-allocation paths are the primary runtime, not a secondary compatibility lane.
3. A temporary rollback switch is allowed for debugging (`UHttpClientOptions.EnableZeroAllocPipeline`, default `true`), but dual-path behavior is not a long-term design goal.
4. File/type references in this phase target the current codebase layout (`Runtime/Core/UHttpClientOptions.cs`, `Runtime/JSON/IJsonSerializer.cs`, `Runtime/Files/MultipartFormDataBuilder.cs`, etc.).
5. Transport/WebGL fallback logic must respect asmdef boundaries: transport assemblies are excluded from WebGL today.
6. TLS provider fallback is **capability-based only** (provider unavailable / platform limitation). Never fall back after authentication or certificate validation failure.

## Why These Enhancements?

| Feature | Problem | Target Outcome |
|---|---|---|
| Buffer pooling completion | Remaining `new byte[]` allocations in HTTP/2 and WebSocket hot paths increase GC churn. | Replace hot-path array allocations with pooled ownership/lifetime discipline. |
| Buffer-writer serialization | JSON and multipart currently allocate intermediate strings/arrays and copy repeatedly. | Serialize directly into pooled writable buffers. |
| Segmented bodies | Growing contiguous buffers amplify LOH pressure and copy costs. | Use segmented buffers and lazy flattening only when required by public APIs. |
| Internal object pooling | Frequent internal object churn under sustained load increases GC pause frequency. | Pool resettable internal objects where ownership is short-lived and clear. |
| TLS provider hardening | Fallback rules can become ambiguous and unsafe if tied to handshake failures. | Keep SslStream-first policy with strict non-downgrade behavior on auth failures. |
| SAEA socket mode | `NetworkStream` async paths can allocate more on some runtimes. | Provide an opt-in SAEA mode for desktop/server performance experiments. |

## Verification Plan

### Performance
- Benchmark sustained high-throughput traffic (target: 10k req/s synthetic workload).
- Steady-state heap growth target after warmup: `< 1 KB/sec` on the HTTP hot path.
- Track per-request allocation deltas with profiler and microbenchmarks.

### Correctness
- All existing tests pass with `EnableZeroAllocPipeline = true`.
- Optional rollback mode (`EnableZeroAllocPipeline = false`) remains functional during alpha hardening.
- Chunked parsing, decompression, and WebSocket fragment assembly remain byte-for-byte correct.

### Safety
- Debug pooled-buffer guards catch use-after-return and double-return defects.
- No data leakage across pooled objects or buffers (headers, auth tokens, payloads).
- TLS downgrade tests confirm no fallback occurs after certificate/auth failures.

### Platform Coverage
- Desktop matrix: Windows, macOS, Linux.
- Mobile validation: iOS/Android IL2CPP for pooled buffer and TLS behavior.
- WebGL remains out-of-scope for Transport assembly work unless asmdef strategy changes first.
