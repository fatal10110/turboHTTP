# Full Core Implementation Review — 2026-02-18

Comprehensive review performed by both specialist agents (unity-infrastructure-architect and unity-network-architect) across all runtime modules.

## Review Scope

All files under `Runtime/` — Core, Transport, JSON, Middleware, Auth, Retry, Observability, Performance, Testing, Cache, RateLimit, Files, Unity. Assembly definitions verified for dependency rules.

---

## Critical Findings (6)

### C-1 [Infra] Namespace/Assembly Mismatch — Middleware Classes in Wrong Namespace

Multiple files across Observability, Retry, Auth, and Cache assemblies declare `namespace TurboHTTP.Middleware` instead of matching their assembly name. Users must write `using TurboHTTP.Middleware;` to access `LoggingMiddleware` from the Observability assembly — misleading and invisible module boundaries.

**Affected files:**
- `Runtime/Observability/LoggingMiddleware.cs` — should be `TurboHTTP.Observability`
- `Runtime/Observability/MetricsMiddleware.cs` — should be `TurboHTTP.Observability`
- `Runtime/Observability/MonitorMiddleware.cs` — should be `TurboHTTP.Observability`
- `Runtime/Retry/RetryMiddleware.cs` — should be `TurboHTTP.Retry`
- `Runtime/Auth/AuthMiddleware.cs` — should be `TurboHTTP.Auth`
- `Runtime/Cache/CacheMiddleware.cs` + partials — should be `TurboHTTP.Cache`

**Fix:** Rename namespaces to match assemblies before first public release. Breaking API change.

---

### C-2 [Infra] `RequestContext.UpdateRequest` Not Thread-Safe

**File:** `Runtime/Core/RequestContext.cs:102`

`Request` property write is not synchronized. On 32-bit IL2CPP, a plain reference write is not guaranteed atomic. The CLAUDE.md claims thread safety for `RequestContext`.

**Fix:** Mark the backing field `volatile`. In practice the pipeline is sequential, so concurrent `UpdateRequest` calls are impossible — but `volatile` costs nothing and matches documented guarantees.

---

### C-3 [Infra] `TurboHTTP.RateLimit` Assembly Has No Implementation Files

**File:** `Runtime/RateLimit/TurboHTTP.RateLimit.asmdef`

The asmdef exists and is referenced by `TurboHTTP.Complete`, but contains zero `.cs` files. Ships as empty assembly.

**Fix:** Implement or remove from the project and `TurboHTTP.Complete.asmdef` references until Phase 8+ work is scheduled.

---

### C-4 [Infra] `TurboHTTP.Transport` Has `autoReferenced: true`

**File:** `Runtime/Transport/TurboHTTP.Transport.asmdef:11`

Should be `false` per the modular architecture. Auto-referencing forces the unsafe-code Transport assembly into every consumer assembly. The `[ModuleInitializer]` fires on first type use regardless of auto-reference status.

**Fix:** Set `"autoReferenced": false`.

---

### C-5 [Network] HTTP/2 Cancellation Use-After-Dispose

**File:** `Runtime/Transport/Http2/Http2Connection.cs:183-189`

The cancellation callback calls `stream.Dispose()` while the read loop may still be writing to `stream.ResponseBody`. If cancellation fires during `HandleDataFrameAsync`, the `MemoryStream` gets disposed mid-write causing `ObjectDisposedException`.

**Scenario:** Cancellation fires → callback removes stream from `_activeStreams` and calls `stream.Dispose()` → read loop's `HandleDataFrameAsync` already retrieved the stream via `TryGetValue` and proceeds to write to `stream.ResponseBody` → `ObjectDisposedException`.

**Fix:** Remove `stream.Dispose()` from the cancellation callback. Just set the TCS as canceled and remove from active streams. Let `SendRequestAsync` handle disposal after observing the cancellation.

---

### C-6 [Network] HTTP/1.1 Request Smuggling via Duplicate Content-Length

**File:** `Runtime/Transport/Http1/Http11RequestSerializer.cs:83-117`

Headers are written to the wire at lines 83-95 *before* Content-Length validation at lines 97-117. Multiple conflicting Content-Length values get serialized before the check catches them. Also, Transfer-Encoding + Content-Length mutual exclusion (RFC 9110 Section 8.6) is not enforced on the serializer side.

**Fix:** Move Content-Length validation and TE+CL mutual exclusion check *before* header serialization begins. Validate all Content-Length values are consistent before any wire output.

---

## Warning Findings (22)

### Infrastructure Warnings

| ID | Issue | File |
|----|-------|------|
| W-I1 | `RetryPolicy.Default` allocates new instance on every access — should be cached singleton | `Runtime/Retry/RetryPolicy.cs:44` |
| W-I2 | `MetricsMiddleware.AddOrUpdate` lambda allocates per request on IL2CPP — use static delegates | `Runtime/Observability/MetricsMiddleware.cs:33,55` |
| W-I3 | HTTP/2 header list allocates `List<(string, string)>` + `ToLowerInvariant()` per request | `Runtime/Transport/Http2/Http2Connection.cs:192-220` |
| W-I4 | CONTINUATION frame payloads use `new byte[]` instead of ArrayPool | `Runtime/Transport/Http2/Http2Connection.Send.cs:32,50` |
| W-I5 | Small frame payloads (4/8 bytes) not pooled on read loop hot path | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:31,61,95` |
| W-I6 | `ObjectPool.Count` volatile read vs lock write inconsistency (cosmetic) | `Runtime/Performance/ObjectPool.cs:25` |
| W-I7 | `SslStreamTlsProvider` uses `Activator.CreateInstance` per connection (IL2CPP overhead) | `Runtime/Transport/Tls/SslStreamTlsProvider.cs:190` |
| W-I8 | `TlsProviderSelector._bouncyCastleAvailable` `bool?` write not atomic — use `Lazy<bool>` | `Runtime/Transport/Tls/TlsProviderSelector.cs:16-55` |
| W-I9 | `ConnectionLease.ReturnToPool` TOCTOU window (documented, mitigated by retry-on-stale) | `Runtime/Transport/Tcp/TcpConnectionPool.cs:152-177` |
| W-I10 | HTTP/2 stream cancellation registration race (idempotent, safe in practice) | `Runtime/Transport/Http2/Http2Connection.cs:183` |
| W-I11 | Read loop `_writeLock.WaitAsync(CancellationToken.None)` vs Dispose (caught) | `Runtime/Transport/Http2/Http2Connection.ReadLoop.cs` |
| W-I12 | `ReadLineAsync` internal static exposes `BufferedStreamReader` — test-only, mark obsolete | `Runtime/Transport/Http1/Http11ResponseParser.cs:243` |
| W-I13 | `CacheMiddleware` dual namespace in same file (follows from C-1) | `Runtime/Cache/CacheMiddleware.cs` |
| W-I14 | `PlatformConfig` duplicates ALPN detection from `SslStreamTlsProvider` | `Runtime/Core/PlatformConfig.cs` |

### Network Warnings

| ID | Issue | File |
|----|-------|------|
| W-N1 | HTTP/2 CONTINUATION frame payloads use `new byte[]` instead of ArrayPool | `Runtime/Transport/Http2/Http2Connection.Send.cs:32,50` |
| W-N2 | Stream ID allocation TOCTOU race under GOAWAY (cosmetic, mitigated by re-check) | `Runtime/Transport/Http2/Http2Connection.cs:155-171` |
| W-N3 | `(SslProtocols)0x3000` TLS 1.3 cast undocumented for maintainers | `Runtime/Transport/Tls/SslStreamTlsProvider.cs:142,198` |
| W-N4 | No background connection scavenger — idle sockets drain battery on mobile | `Runtime/Transport/Tcp/TcpConnectionPool.cs:291-311` |
| W-N5 | HPACK dynamic table uses `List.Insert(0)` — O(n) per insertion | `Runtime/Transport/Http2/HpackDynamicTable.cs:61` |
| W-N6 | `UHttpRequest.Timeout` is per-attempt, not global (undocumented user surprise) | `Runtime/Retry/RetryMiddleware.cs:39-91` |
| W-N7 | HTTP/2 `Dispose()` may cause `ObjectDisposedException` in concurrent senders (caught) | `Runtime/Transport/Http2/Http2Connection.Lifecycle.cs:136-150` |
| W-N8 | `Socket.Available` can't detect bytes buffered inside SslStream (documented best-effort) | `Runtime/Transport/Tcp/TcpConnectionPool.cs:524-534` |

**Note:** W-I4 and W-N1 are the same finding (CONTINUATION frame allocations) reported by both reviewers.

---

## Info Findings (21)

### Infrastructure Info

| ID | Summary |
|----|---------|
| I-I1 | `HttpHeaders.GetEnumerator()` yields only first value per header name (documented) |
| I-I2 | `RequestContext.State` copies entire dictionary on every read (intentional snapshot) |
| I-I3 | `Http2Stream.GetHeaderBlock()` uses `ToArray()` — allocates full copy |
| I-I4 | HTTP/1.1 serializer overly strict on Content-Length whitespace parsing |
| I-I5 | `BufferedStreamReader.EnsureCapacity` theoretical pool leak (infallible in practice) |
| I-I6 | `Http2ConnectionManager.Dispose` has no `_disposed` guard (layered defense in transport) |
| I-I7 | `TurboHTTP.Complete` should exclude WebGL (references Transport which excludes it) |
| I-I8 | `MockTransport.EnqueueJsonResponse` uses reflection for optional JSON dependency |
| I-I9 | `LoggingMiddleware` uses `DateTime.UtcNow` instead of `context.Elapsed` |
| I-I10 | CLAUDE.md says ObjectPool uses Interlocked but implementation uses lock |

### Network Info

| ID | Summary |
|----|---------|
| I-N1 | `StringBuilder(256)` allocation per request in HTTP/1.1 serializer (Phase 6 target) |
| I-N2 | No client-initiated PING keepalive for HTTP/2 (stale connections on mobile) |
| I-N3 | HPACK encoder allocates raw bytes per header value |
| I-N4 | Frame codec issues two separate `WriteAsync` calls (header + payload) |
| I-N5 | `ObjectPool<T>` uses lock instead of documented Interlocked fast path |
| I-N6 | `CacheMiddleware` uses LINQ `.Any()` with lambda closures |
| I-N7 | Connection lease ReturnToPool race window (documented and handled) |
| I-N8 | HTTP/2 user-agent header check is case-insensitive (confirmed correct) |
| I-N9 | Huffman decoder uses `List<byte>.ToArray()` — allocations under load |
| I-N10 | SETTINGS_MAX_FRAME_SIZE validation uses correct error code |
| I-N11 | Platform `#if` directives correctly structured |

---

## Protocol Correctness Summary

### HTTP/1.1 (RFC 9110/9112) — PASS
- Request line format, Host header, Content-Length, chunked encoding, 1xx handling, HEAD response, CRLF injection prevention, status code parsing — all correct.
- **Exception:** C-6 (Content-Length validation ordering) and TE+CL mutual exclusion.

### HTTP/2 (RFC 9113) — PASS
- Connection preface, SETTINGS, flow control, HPACK, GOAWAY, RST_STREAM, PUSH_PROMISE rejection, forbidden headers, pseudo-headers, stream multiplexing — all correct.
- **Exception:** C-5 (cancellation use-after-dispose race).

### HPACK (RFC 7541) — PASS
- Integer encoding/decoding, Huffman, dynamic table, size update ordering, static table — all correct.

---

## Platform Compatibility — SOUND

- **IL2CPP:** Reflection cached in static constructors with graceful fallbacks. Latin1 encoding fallback for code stripping. ValueTuple, ConcurrentDictionary, async/await all AOT-safe.
- **WebGL:** Correctly excluded via asmdef `excludePlatforms`.
- **.NET Standard 2.1:** All API surface used is available. `Dns.GetHostAddressesAsync` timeout via `Task.WhenAny` (no CT overload). `SslStream` ALPN accessed via reflection.

---

## Security — SOUND (with exceptions)

- Certificate validation strict (`SslPolicyErrors.None` required)
- TLS 1.2 minimum enforced
- CRLF injection validated in serializer
- HPACK decompression bomb protection (256KB limit)
- Max response body size (100MB) and max header size limits
- **Exception:** C-6 (request smuggling via duplicate Content-Length)

---

## Assembly Boundary Verification

| Assembly | `autoReferenced` | References Only Core | No Cross-Module | Status |
|----------|-----------------|---------------------|----------------|--------|
| TurboHTTP.Core | true | N/A | N/A | OK |
| TurboHTTP.Transport | **true (should be false)** | yes | yes | **C-4** |
| TurboHTTP.JSON | false | yes | yes | OK |
| TurboHTTP.Auth | false | yes | yes | OK |
| TurboHTTP.Retry | false | yes | yes | OK |
| TurboHTTP.Cache | false | yes | yes | OK |
| TurboHTTP.Observability | false | yes | yes | OK |
| TurboHTTP.Performance | false | yes | yes | OK |
| TurboHTTP.Testing | false | yes | yes | OK |
| TurboHTTP.Files | false | yes | yes | OK |
| TurboHTTP.Unity | false | yes | yes | OK |
| TurboHTTP.Middleware | false | yes | yes | OK |
| TurboHTTP.RateLimit | false | yes | N/A (empty) | **C-3** |
| TurboHTTP.Complete | true | refs all | N/A | OK (needs WebGL exclude) |

---

## Prioritized Fix List

| # | ID | Severity | Description |
|---|-----|----------|-------------|
| 1 | C-1 | Critical | Fix namespace mismatches across 6+ middleware files |
| 2 | C-5 | Critical | HTTP/2 cancellation use-after-dispose on stream |
| 3 | C-6 | Critical | Request smuggling — validate Content-Length before serialization |
| 4 | C-4 | Critical | Set Transport `autoReferenced: false` |
| 5 | C-3 | Critical | Remove empty RateLimit assembly |
| 6 | C-2 | Critical | Make `RequestContext.Request` volatile |
| 7 | W-I1 | Warning | Cache `RetryPolicy.Default` as static singleton |
| 8 | W-I8 | Warning | Fix nullable bool race in `TlsProviderSelector` |
| 9 | W-I2 | Warning | Use static delegates in `MetricsMiddleware.AddOrUpdate` |
| 10 | I-I7 | Info | Add `excludePlatforms: ["WebGL"]` to `TurboHTTP.Complete` |

---

## Overall Assessment

The codebase is well-architected with strong assembly boundaries, solid RFC compliance (HTTP/1.1, HTTP/2, HPACK), and robust IL2CPP/AOT fallback strategies. The 6 critical findings are pre-release blockers — 2 are concurrency/security edge cases in the transport layer, and 4 are structural issues (namespaces, assembly config, empty module). The warnings are primarily allocation optimizations and minor race condition documentation. None of the warnings block production use for typical workloads.
