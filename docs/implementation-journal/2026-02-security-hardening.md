# Security & Network Hardening (Post-Phase 6 Review)

**Date:** 2026-02-15
**Phase:** Post-M1 hardening (between Phase 6 and Phase 7)

## Summary

Applied fixes from the unified security/network/architecture review conducted after Phase 6 completion. All CRITICAL and HIGH issues were deferred by user decision (BouncyCastle TLS cert validation, CRL/OCSP). All MEDIUM and LOW issues were either fixed or documented.

## Fixes Applied

### Security MEDIUM

| ID | Issue | Fix | File |
|----|-------|-----|------|
| M-2 | Pooled connection reuse without drain | Added `HasUnexpectedData()` check using `Socket.Available` before reusing idle connections. Connections with stale data are disposed. | `Runtime/Transport/Tcp/TcpConnectionPool.cs` |
| M-3 | TE+CL ambiguity in response parser | Remove `Content-Length` header when `Transfer-Encoding: chunked` is present (RFC 9112 Section 6.1) | `Runtime/Transport/Http1/Http11ResponseParser.cs` |
| M-4 | FileDownloader path traversal | Added `BasePath` property with path canonicalization and traversal validation using `Path.GetFullPath()` | `Runtime/Files/FileDownloader.cs` |
| M-5 | StaticTokenProvider plaintext storage | Added XML doc security note about plaintext credential retention in managed memory | `Runtime/Auth/StaticTokenProvider.cs` |

### Security HIGH (builder level)

| ID | Issue | Fix | File |
|----|-------|-----|------|
| H-3 | CRLF injection via WithHeader | Added `ValidateHeaderInput()` that rejects CR/LF in header names and values. Defense-in-depth alongside serializer validation. | `Runtime/Core/UHttpRequestBuilder.cs` |

### Security LOW

| ID | Issue | Fix | File |
|----|-------|-----|------|
| L-1 | Misleading cert validation comment | Fixed comment to accurately describe OS-level validation behavior | `Runtime/Transport/Tls/SslStreamTlsProvider.cs` |
| L-2 | Header name tchar validation | Deferred to Phase 10 (TODO already documented). Current CRLF/colon check is sufficient for security. | `Runtime/Transport/Http1/Http11RequestSerializer.cs` |
| L-3 | Non-atomic MaxConcurrentStreams | Deferred to Phase 10 (documented). Race is benign — worst case is one extra stream. | N/A |
| L-4 | DNS timeout task not observed | Added `ContinueWith(OnlyOnFaulted)` to suppress `UnobservedTaskException` from abandoned DNS tasks | `Runtime/Transport/Tcp/TcpConnectionPool.cs` |
| L-5 | Multipart boundary not quoted | Quoted boundary in Content-Type header; removed space from valid boundary chars | `Runtime/Files/MultipartFormDataBuilder.cs` |

### Network Issues

| Issue | Fix | File |
|-------|-----|------|
| HPACK decompression bomb | Added `MaxDecodedHeaderBytes` (128KB) limit per header block decode. Tracks cumulative name+value byte count and throws `HpackDecodingException` on overflow. | `Runtime/Transport/Http2/HpackDecoder.cs` |
| SETTINGS_ENABLE_PUSH = 0 | Already implemented in `Http2Settings.SerializeClientSettings()` line 89. Review finding was incorrect. | N/A |
| IPv6 dual-stack | Added `Array.Sort` to prefer IPv6 addresses before IPv4 in `ConnectSocketAsync`. Full Happy Eyeballs (RFC 8305) deferred to Phase 14. | `Runtime/Transport/Tcp/TcpConnectionPool.cs` |

### Deferred Items

| Issue | Target | Reason |
|-------|--------|--------|
| C-1: BouncyCastle TLS cert validation | Later phases | User decision — BouncyCastle is fallback-only |
| H-1: CRL/OCSP disabled | Later phases | User decision — OS-level validation is standard |
| M-1: No redirect handling | Phase 10 | Added Task 10.8 to phase docs |
| DNS cache | Phase 10 | User decision |
| Full Happy Eyeballs (RFC 8305) | Phase 14 | IPv6-first sorting implemented as interim |

### Phase Doc Updates

- **Phase 10:** Added Task 10.8 (Redirect Middleware), Task 10.9 (Cookie Middleware), Task 10.10 (Streaming Transport Improvements)
- **Phase 14:** Added Section 7 (Background Networking on Mobile), added to prioritization matrix and v1.1 roadmap. Proxy support already present (Section 4). Renumbered sections 8-16.

## Decisions

1. **Defense-in-depth for CRLF:** Validation happens at both `UHttpRequestBuilder.WithHeader()` and `Http11RequestSerializer.ValidateHeader()`. The builder check catches injection early with clear error messages; the serializer check is the last line of defense.

2. **Connection drain vs. dispose:** Chose to dispose connections with unexpected data rather than drain. Draining is risky — a malicious server could send arbitrary data to exhaust resources.

3. **HPACK bomb limit:** 128KB matches common server limits (Apache, nginx). A legitimate HTTP/2 response rarely exceeds 16KB of headers. The limit is per header block, not per connection.

4. **IPv6 preference:** Simple sort (IPv6 first) provides the key benefit without Happy Eyeballs complexity. On networks with broken IPv6, the sequential fallback adds latency but still succeeds.

5. **Multipart boundary quoting:** Always quote the boundary in `Content-Type` to handle edge cases. Removed space from valid boundary chars since it's problematic even when quoted and generated boundaries never contain spaces.
