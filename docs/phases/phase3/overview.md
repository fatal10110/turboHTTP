# Phase 3 Implementation Plan — Overview

Phase 3 is broken into 5 sub-phases executed sequentially. Each sub-phase is self-contained with its own files, tests, review, and verification criteria.

## Sub-Phase Index

| Sub-Phase | Name | Files | Depends On |
|---|---|---|---|
| [3.1](phase-3.1-client-api.md) | Client API (Core) | 5 new + 1 modified | Phase 2 |
| [3.2](phase-3.2-tcp-tls.md) | TCP Connection Pool & TLS | 2 new | Phase 2 |
| [3.3](phase-3.3-http11-protocol.md) | HTTP/1.1 Serializer & Parser | 2 new | Phase 2 |
| [3.4](phase-3.4-transport-wiring.md) | RawSocketTransport & Wiring | 3 new (RawSocketTransport + ModuleInitializer + ModuleInitializerAttribute polyfill, all in Transport) | 3.1, 3.2, 3.3 |
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

1. **Error model:** Transport maps platform exceptions to `UHttpException` (it has the best context to distinguish DNS timeout vs socket error vs TLS failure). Client re-throws `UHttpException` as-is (`catch (UHttpException) { throw; }`). Client only wraps unexpected non-`UHttpException` exceptions as a safety net. HTTP 4xx/5xx returned as normal responses with body intact. **CRITICAL: The `catch (UHttpException) { throw; }` handler MUST be the first catch clause in both `RawSocketTransport.SendAsync` and `UHttpClient.SendAsync`. Reordering will cause double-wrapping.**
2. **`ConfigureAwait(false)`** on all transport-layer awaits.
3. **IPv6:** Explicit DNS resolution, use resolved address's `AddressFamily` (NOT hardcoded `InterNetwork`). Required for Apple App Store (IPv6-only network test).
4. **Multi-value headers:** Serializer iterates `Names` + `GetValues()`. Parser uses `Add()` not `Set()`.
5. **No `ReadOnlyMemory<byte>`** yet — keep `byte[]`, document for Phase 10.
6. **Redirect following:** Options defined but NOT enforced — deferred to Phase 4.
7. **Connection lifecycle:** `ConnectionLease` (IDisposable **class**, not struct — mutable structs break dispose semantics due to value-type copy) wraps connection + semaphore to guarantee per-host permit is always released. Idempotent `Dispose()` prevents double semaphore release. Prevents deadlock on non-keepalive responses, exceptions, and cancellation paths. `IsAlive` check performed outside the lock to avoid holding locks during `Socket.Poll()` syscalls.
8. **Header injection validation:** ALL header names and values validated for CRLF injection during serialization (not just Host). Header names also validated for empty/whitespace. Prevents HTTP response splitting attacks.
9. **Response body size limit:** `MaxResponseBodySize = 100MB` enforced in chunked, fixed-length, and read-to-end body readers. Prevents `OutOfMemoryException` from malicious/misconfigured servers.
10. **TLS strategy:** `SslProtocols.None` (OS-negotiated, Microsoft-recommended) with post-handshake TLS 1.2 minimum enforcement. Avoids `SslProtocols.Tls13` crashes on older iOS/macOS. Negotiated TLS version stored on `PooledConnection.NegotiatedTlsVersion` for testability.
11. **Encoding:** `Encoding.GetEncoding(28591)` cached in static field, with custom `Latin1Encoding` fallback for IL2CPP code stripping. `Encoding.Latin1` static property is .NET 5+ only, NOT .NET Standard 2.1. **Both serializer and parser use the same `Latin1` static field** — parser's `ReadLineAsync` reads raw bytes and converts via `Latin1.GetString()`, NOT implicit char cast or UTF-8.
12. **KeepAlive on ReadToEnd:** Forced `false` when body was read via `ReadToEndAsync` (no Content-Length, no chunked). Connection read to EOF is dead and cannot be reused.
13. **Timeout enforcement is best-effort for certain operations:** See "Known Limitations" below.
14. **Transport registration:** C# 9 `[ModuleInitializer]` in `TurboHTTP.Transport` assembly — auto-registers `RawSocketTransport` when assembly loads. No Unity bootstrap assembly needed; `TurboHTTP.Unity` stays optional and Core-only. **IL2CPP note:** Module initializer timing is implementation-defined under IL2CPP. `RawSocketTransport.EnsureRegistered()` fallback documented. IL2CPP build test is Phase 3.5 mandatory gate.
15. **Transport factory:** Uses `Lazy<T>` with `LazyThreadSafetyMode.ExecutionAndPublication` for thread-safe singleton initialization. `Default` getter is **lock-free** (volatile read + `Lazy<T>.Value` handles thread safety internally). Lock is only taken during `Register()`. No race conditions on concurrent first access.
16. **TLS handshake:** Single cached `MethodInfo` probe for `SslClientAuthenticationOptions` overload (`.NET 5+`), invoked via reflection to avoid compile-time dependency. Falls back to 4-arg overload with `Task.WhenAny`-based cancellation if unavailable (avoids dispose-on-cancel race). ALPN requires the primary path.
17. **DNS timeout:** 5-second `Task.WhenAny` wrapper around `Dns.GetHostAddressesAsync`. Background task continues after timeout (unavoidable in .NET Std 2.1). Under pathological DNS failures (mobile network loss), background DNS tasks can accumulate — inherent .NET Standard 2.1 limitation.
18. **User-Agent:** Auto-added `User-Agent: TurboHTTP/1.0` unless user sets one. Many CDNs/servers block requests without it.
19. **Content-Length validation:** Serializer validates user-set `Content-Length` matches `request.Body.Length`. Mismatch throws `ArgumentException`.
20. **Transfer-Encoding precedence:** Per RFC 9112 §6.1, `Transfer-Encoding` takes precedence over `Content-Length`. `gzip, chunked` accepted (returns raw compressed chunks). Multiple conflicting `Content-Length` values throw `FormatException`.
21. **Transfer-Encoding / Content-Length mutual exclusion (request serializer):** Per RFC 9110 §8.6, if user sets `Transfer-Encoding` header, serializer does NOT auto-add `Content-Length`. If user sets `Transfer-Encoding: chunked` with a `byte[]` body, throws `ArgumentException` (chunked body encoding not implemented in Phase 3).
22. **Transport ownership:** Factory-provided transports are **shared singletons** — never disposed by any individual `UHttpClient`. Only user-provided transports with `UHttpClientOptions.DisposeTransport = true` are disposed by the client. This prevents the first client's `Dispose()` from breaking all other clients sharing the factory singleton.
23. **URI validation:** `RawSocketTransport.SendAsync` validates `request.Uri.IsAbsoluteUri` before processing. Relative URIs throw `UHttpException(InvalidRequest)`.
24. **Retry-on-stale architecture:** Single-attempt logic extracted into `SendOnLeaseAsync` helper method. Retry wrapper disposes stale lease, acquires fresh lease, calls helper again. Prevents variable scoping bugs between original and fresh leases.
25. **Pool disposal safety:** `GetConnectionAsync` and `EnqueueConnection` check `_disposed` flag at entry. Prevents post-dispose operations from creating orphaned connections or throwing unexpected exceptions.
26. **Semaphore eviction safety:** Eviction drains idle connections but does NOT dispose semaphores (race-safe). Other threads may still reference evicted semaphores via `GetOrAdd`. Semaphores are only disposed in `TcpConnectionPool.Dispose()`. Memory cost is trivial (~100 bytes per orphaned semaphore).
27. **Connection liveness (`IsAlive`):** Best-effort only — MUST NOT be relied upon for correctness. For TLS connections, `Socket.Available` does not reflect SslStream's internal buffering. Retry-on-stale is the true safety net.
28. **Builder timeout propagation:** `UHttpRequestBuilder.Build()` uses `_client._options.DefaultTimeout` as timeout unless `WithTimeout()` was explicitly called. The hardcoded 30s in `UHttpRequest` constructor only applies when constructing requests directly (without builder).
29. **`LastUsed` atomicity:** `PooledConnection._lastUsedTicks` is a `long` field accessed via `Interlocked.Read/Exchange`. C# does not allow `volatile` on `long` (CS0677). `Interlocked` provides both atomicity and memory ordering on all platforms including ARM32 IL2CPP.
30. **Connection reuse tracking (`IsReused`):** `PooledConnection.IsReused` (bool) is set to `true` when dequeued from idle pool. Retry-on-stale only triggers for `IOException` when `lease.Connection.IsReused` is `true` — fresh connections do not retry.
31. **IL2CPP stripping protection:** `Runtime/Transport/link.xml` preserves `SslStream`, `SslClientAuthenticationOptions`, and codepage encodings from managed code stripping. Required for reflection probe and `Encoding.GetEncoding(28591)` to work under IL2CPP.
32. **Shared Latin-1 encoding:** Both serializer and parser use a shared `EncodingHelper.Latin1` static field (in `Runtime/Transport/Internal/EncodingHelper.cs`) to avoid duplicating initialization and fallback logic.
33. **`ParsedResponse` visibility:** `internal class` (not `public`) — implementation detail of the Transport assembly, consumed only by `RawSocketTransport`.
34. **`FormatException` mapping:** Parser throws `FormatException` for malformed HTTP responses (invalid chunk hex, bad status lines, header size exceeded). Transport maps these to `UHttpException(NetworkError)` — they indicate a broken server response, not a programming error.
35. **TLS version enforcement exception type:** Post-handshake TLS minimum (1.2) check throws `AuthenticationException` (not `SecurityException`). `AuthenticationException` is already caught by `RawSocketTransport`'s exception handlers and mapped to `UHttpErrorType.CertificateError`. `SecurityException` would fall through to the generic `Exception` handler and be incorrectly reported as `Unknown`.
36. **`TlsStreamWrapper.WrapAsync` return type:** Returns `TlsResult` struct (not just `Stream`) containing both the wrapped stream and `SslProtocols` negotiated version. This allows `TcpConnectionPool` to set `PooledConnection.NegotiatedTlsVersion` without needing access to the `SslStream` instance hidden behind the `Stream` type.
37. **Retry-on-stale idempotency guard:** Retry only fires for idempotent methods (`request.Method.IsIdempotent()` — GET, HEAD, PUT, DELETE, OPTIONS). Non-idempotent methods (POST, PATCH) on stale connections throw `IOException` (mapped to `NetworkError`) without retry, preventing duplicate side effects.
38. **Chunk size narrowing:** Chunk sizes are parsed as `long` (hex values can exceed `int.MaxValue`), validated against `MaxResponseBodySize` (100MB, fits in `int`), then narrowed to `int` for `MemoryStream`/array operations. The `MaxResponseBodySize` check is the guard — if a chunk exceeds it, `IOException` is thrown before any narrowing occurs.

## Known Limitations (deferred)

| Limitation | Target | Notes |
|---|---|---|
| StringBuilder allocation in serializer (~600-700 bytes GC) | Phase 10 | Rewrite with ArrayPool<byte> |
| Byte-by-byte ReadLineAsync (~400 Task allocs per response, ~29KB) | Phase 10 | Replace with buffered reader (4KB+ chunks). Dominates Phase 3 GC budget. |
| No gzip/deflate decompression | Phase 5/6 | `Transfer-Encoding: gzip, chunked` returns raw compressed chunks; callers can decompress manually |
| No 100-continue auto-send | Phase 6 | |
| No background idle connection cleanup | Phase 10 | Stale connections handled by retry-on-stale |
| System.Text.Json AOT risk | Phase 9 | `WithJsonBody(string)` overload available as IL2CPP-safe alternative. `WithJsonBody<T>()` documented as Editor/Mono only. |
| No redirect following | Phase 4 | Options defined but not enforced |
| byte[] body (not ReadOnlyMemory) | Phase 10 | |
| No request body streaming | Phase 5 | |
| DNS resolution not cancellable (.NET Standard 2.1) | Phase 3 (mitigated) | Wrapped with 5-second `Task.WhenAny` timeout. Background DNS task continues after timeout (unavoidable). |
| Socket.ConnectAsync not cancellable (.NET Standard 2.1) | Phase 3 (mitigated) | Uses `ct.Register(() => socket.Dispose())` pattern for best-effort cancellation. |
| Timeout enforcement best-effort for DNS + connect | Documented | DNS: 5-second timeout wrapper. Connect: dispose-on-cancel. TLS handshake: cancellable via `SslClientAuthenticationOptions` overload (reflection) if available, otherwise `Task.WhenAny`-based cancellation fallback. |
| Chunked trailer headers consumed but discarded | Phase 6 | Read but not merged into response headers |
| SemaphoreSlim per-host capped at 1000 entries | Phase 10 | Basic eviction of idle entries when cap exceeded. Full LRU cleanup deferred. |
| MemoryStream.ToArray() copies response body | Phase 10 | Use TryGetBuffer + pooled MemoryStream |
| Full Happy Eyeballs (RFC 8305) | Phase 10 | Phase 3 tries addresses sequentially |
| `Encoding.Latin1` property (.NET 5+ only) | Phase 3 (mitigated) | Uses `Encoding.GetEncoding(28591)` with custom Latin1Encoding fallback for IL2CPP stripping. |
| `SslClientAuthenticationOptions` overload (.NET 5+ only) | Phase 3 (mitigated) | Runtime probe via cached `MethodInfo`, invoked via reflection. Fallback to 4-arg overload with `Task.WhenAny` cancellation. ALPN unavailable through fallback path. |
| Certificate revocation checking disabled | Phase 9 | Both TLS paths disable CRL/OCSP checks. Revoked certificates will be accepted. CRL distribution points can be unreachable, causing connection failures — disabled for reliability. CRL/OCSP support deferred to Phase 9 (security hardening). |
| System.Text.Json availability in Unity 2021.3 | Documented | Depends on Unity version's BCL. If unavailable, only `WithJsonBody(string)` overload works. Users can bring their own serializer. |
| `[ModuleInitializer]` IL2CPP timing | Phase 3 (mitigated) | `RawSocketTransport.EnsureRegistered()` fallback documented. IL2CPP build test is Phase 3.5 gate. |
| `ModuleInitializerAttribute` not in .NET Std 2.1 BCL | Phase 3 (mitigated) | Polyfill in `Runtime/Transport/Internal/ModuleInitializerAttribute.cs`. `internal` + `#if !NET5_0_OR_GREATER` guard prevents conflicts. |
| Retry-on-stale releases semaphore, re-competes | Phase 10 | Under high concurrency, retry request competes with waiters. Permit-preserving retry deferred. Phase 3.5 should measure P99 latency under saturation. |
| `HttpStatusCode` enum range gaps | Documented | Status codes not in enum (e.g., 451, 425) stored as raw int cast. Consumers should use int range checks. `ParsedResponse.StatusCode` and `UHttpResponse.StatusCode` document this behavior. |
| Certificate revocation disabled (security risk) | Phase 9 | TLS handshake accepts revoked certificates (no CRL/OCSP). Rationale: CRL endpoints unreachable on mobile. Risk: MITM with compromised-but-not-expired certs. Phase 9 adds opt-in OCSP stapling + certificate pinning. |
| Byte-by-byte SslStream reads under IL2CPP | Phase 3.5 (validate) / Phase 10 (fix) | Forces 16KB TLS record decryption per byte. SslStream internal buffering may differ under IL2CPP. Phase 3.5 IL2CPP gate must include multi-header response validation. Phase 10 replaces with buffered reader. |
| Semaphore eviction leaves orphaned SemaphoreSlim | Phase 10 | Evicted semaphores are not disposed (race-safe). ~100 bytes each. Phase 10 implements quiescence-checked disposal. |
| DNS background task accumulation | Documented | Under pathological DNS failures, timed-out DNS tasks continue in background. Inherent .NET Std 2.1 limitation. Phase 10 DNS caching amortizes cost. |
| HTTP obs-fold (header continuation lines) | Documented | RFC 9110 deprecated obs-fold (header lines starting with SP/HTAB as continuation of previous header). Parser does not handle obs-fold — continuation lines are treated as separate malformed headers (missing colon → silently dropped). Most modern servers do not send obs-fold, but some legacy proxies still do. Phase 6 or later may add obs-fold parsing if user reports indicate need. |
| Retry-on-stale limited to idempotent methods | Documented | POST/PATCH on stale connections throw `IOException` (mapped to `NetworkError`) without retry. Application-level retry needed for non-idempotent operations. |

## GC Target

Phase 3 targets **~50KB GC per request** (correctness focus, **NOT performance optimized yet** — intentionally high). The dominant cost is byte-by-byte `ReadAsync` in the response parser, which creates ~400 `Task<int>` allocations (~29KB) per response for headers alone. Combined with StringBuilder serialization (~600-700 bytes), header string parsing (~400 bytes), and other allocations, the realistic total is 30-50KB per request.

The previous target of <2KB was based on an underestimate that did not account for Task allocations from byte-by-byte reading. **Phase 10 targets <500 bytes** with buffered reader (4KB+ chunks), ArrayPool, and zero-alloc patterns — this is the production target. The 50KB Phase 3 budget is temporary and will be eliminated. See CLAUDE.md "Critical Risk Areas" for details.

## Post-Phase Review

After all sub-phases complete, run both specialist agents per CLAUDE.md:
- **unity-infrastructure-architect**: architecture, memory, thread safety, IL2CPP
- **unity-network-architect**: protocol correctness, platform compat, TLS/security
