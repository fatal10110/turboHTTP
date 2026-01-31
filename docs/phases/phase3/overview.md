# Phase 3 Implementation Plan — Overview

Phase 3 is broken into 5 sub-phases executed sequentially. Each sub-phase is self-contained with its own files, tests, review, and verification criteria.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [3.1](phase-3.1-client-api.md) | Client API (Core) | 5 new + 1 modified | Phase 2 |
| [3.2](phase-3.2-tcp-tls.md) | TCP Connection Pool & TLS | 2 new | Phase 2 |
| [3.3](phase-3.3-http11-protocol.md) | HTTP/1.1 Serializer & Parser | 2 new | Phase 2 |
| [3.4](phase-3.4-transport-wiring.md) | RawSocketTransport & Wiring | 2 new (Transport + Unity bootstrap) | 3.1, 3.2, 3.3 |
| [3.5](phase-3.5-tests-integration.md) | Tests & Integration | 5 new + CLAUDE.md | 3.4 |

## Dependency Graph

```
Phase 2 (done)
    ├── 3.1 Client API (Core)
    ├── 3.2 TCP + TLS (Transport)
    ├── 3.3 HTTP/1.1 Protocol (Transport)
    └──────────┬───────────────
               3.4 RawSocketTransport Wiring
               │
               3.5 Tests & Integration
```

Sub-phases 3.1, 3.2, and 3.3 can be implemented in parallel (no inter-dependencies). Sub-phase 3.4 wires them together. Sub-phase 3.5 tests everything.

## Existing Foundation (Phase 2)

Files in `Runtime/Core/` (all implemented):
- `HttpMethod.cs`, `HttpHeaders.cs`, `UHttpRequest.cs`, `UHttpResponse.cs`
- `UHttpError.cs` / `UHttpException`, `RequestContext.cs`
- `IHttpTransport.cs`, `HttpTransportFactory.cs`

Assembly defs: `TurboHTTP.Core.asmdef` (no refs), `TurboHTTP.Transport.asmdef` (refs Core, excludes WebGL, allowUnsafeCode, noEngineReferences).

## Cross-Cutting Design Decisions

1. **Error model:** Transport throws exceptions. Client catches and wraps in `UHttpException`. HTTP 4xx/5xx returned as normal responses with body intact.
2. **`ConfigureAwait(false)`** on all transport-layer awaits.
3. **IPv6:** Explicit DNS resolution, use resolved address's `AddressFamily`.
4. **Multi-value headers:** Serializer iterates `Names` + `GetValues()`. Parser uses `Add()` not `Set()`.
5. **No `ReadOnlyMemory<byte>`** yet — keep `byte[]`, document for Phase 10.
6. **Redirect following:** Options defined but NOT enforced — deferred to Phase 4.

## Known Limitations (deferred)

| Limitation | Target | Notes |
|---|---|---|
| StringBuilder allocation in serializer (~600-700 bytes GC) | Phase 10 | Rewrite with ArrayPool<byte> |
| Byte-by-byte ReadLineAsync (~600 Task allocs per response) | Phase 10 | Replace with buffered reader (4KB+ chunks) |
| No gzip/deflate decompression | Phase 5/6 | |
| No 100-continue auto-send | Phase 6 | |
| No background idle connection cleanup | Phase 10 | Stale connections handled by retry-on-stale |
| System.Text.Json AOT risk | Phase 9 | `WithJsonBody(string)` overload available as IL2CPP-safe alternative |
| No redirect following | Phase 4 | Options defined but not enforced |
| byte[] body (not ReadOnlyMemory) | Phase 10 | |
| No request body streaming | Phase 5 | |
| DNS resolution not cancellable (.NET Standard 2.1) | Phase 10 | Can hang on mobile; see CLAUDE.md |
| Chunked trailer headers consumed but discarded | Phase 6 | Read but not merged into response headers |
| SemaphoreSlim per-host never cleaned up | Phase 10 | Slow leak for apps hitting many unique hosts |
| host.ToLowerInvariant() allocates per pool key lookup | Phase 10 | Use StringComparer.OrdinalIgnoreCase instead |
| MemoryStream.ToArray() copies response body | Phase 10 | Use TryGetBuffer + pooled MemoryStream |
| Full Happy Eyeballs (RFC 8305) | Phase 10 | Phase 3 tries addresses sequentially |

## GC Target

Phase 3 targets **<2KB GC per request** (correctness focus). Phase 10 targets **<500 bytes** with zero-alloc patterns. See CLAUDE.md "Critical Risk Areas" for details.

## Post-Phase Review

After all sub-phases complete, run both specialist agents per CLAUDE.md:
- **unity-infrastructure-architect**: architecture, memory, thread safety, IL2CPP
- **unity-network-architect**: protocol correctness, platform compat, TLS/security
