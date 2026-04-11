# Phase 22c: HTTP/2 Through CONNECT Proxy Tunnels — Overview

**Milestone:** M4 (v2.0 follow-up)
**Dependencies:** Phase 22a (end-to-end streaming), Phase 22b.2 (streaming through proxy connections)
**Estimated Complexity:** High
**Critical:** No — HTTP/2 through proxies is a performance optimization, not a correctness fix. HTTP/1.1 through CONNECT tunnels works correctly today.
**Compatibility:** Additive. No breaking changes. Direct HTTP/2 connections are unaffected.

> **Source document:** The detailed plan lives at `Development/docs/phases/phase-22c-proxy-http2-alpn.md`. This directory breaks the plan into per-sub-phase implementation files.

## Context

When TurboHTTP connects to an HTTPS origin through a CONNECT proxy, the TLS handshake in
`EstablishConnectTunnelAsync` hardcodes `new[] { "http/1.1" }` for ALPN, deliberately
suppressing HTTP/2 negotiation. After the tunnel, the secure proxy path stays inside the 22b.2
lease-based HTTP/1.1 helper flow (`DispatchProxyHttp11WithRetryAsync` /
`AcquirePreparedProxyLeaseAsync`) with no ALPN check, no `Http2ConnectionManager` integration,
and no protocol routing.

This means every request through a proxy pays the full TCP + CONNECT + TLS cost independently instead of amortizing it across multiplexed HTTP/2 streams.

Phase 22c enables HTTP/2 ALPN negotiation through CONNECT tunnels, adds protocol routing to the proxy dispatch path, and extends `Http2ConnectionManager` to track proxy-tunneled HTTP/2 connections via an origin×proxy compound key.

## Core Design Principles

1. **Origin × Proxy Key Scheme** — The `Http2ConnectionManager` key for tunneled connections is `"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}"`. Direct-connection keys (`"{host}:{port}"`) are unchanged. The `|via|` separator is unambiguous because `|` is not valid in hostnames (RFC 952).
2. **Additive Overloads Only** — Direct-connection methods (`GetIfExists(host, port)`, etc.) are not changed. Proxy-tunneled variants are new overloads. No regression risk on the hot path.
3. **Fast Path Before CONNECT** — `DispatchViaProxyAsync` checks `_h2Manager.GetIfExists(host, port, proxyHost, proxyPort)` before establishing any tunnel. Subsequent requests to the same origin through the same proxy skip DNS, TCP, CONNECT, and TLS entirely.
4. **Preserve 22b.2 Lease-Based HTTP/1.1 Semantics** — The tunneled HTTP/1.1 fallback must stay on the existing prepared-lease path so early-dispose, stale-retry, and tunnel pool-key reuse behavior do not regress.
5. **Connection Lease Transfer** — The proxy connection lease's `TransferOwnership()` releases the per-proxy semaphore permit and hands the TCP connection to `Http2Connection` for its entire multiplexed lifetime. Same model as direct connections.
6. **BouncyCastle — Pre-Handshake Provider Selection Only** — BouncyCastle is used only when SslStream is genuinely unavailable (stripped from the IL2CPP build). `TlsProviderSelector.GetProvider` returns the correct provider before the TLS handshake begins. TLS failures through the tunnel propagate as-is; there is no runtime catch-and-retry that switches providers mid-request.

## Sub-Phase Index

| Sub-Phase | Name | Effort | Depends On |
|-----------|------|--------|------------|
| [22c.1](phase-22c.1-manager-key-extension.md) | `Http2ConnectionManager` Key Extension | 1–2 days | — |
| [22c.2](phase-22c.2-tunnel-alpn-extension.md) | `EstablishConnectTunnelAsync` ALPN Extension | 1–2 days | — |
| [22c.3](phase-22c.3-protocol-routing.md) | Protocol Routing in `DispatchViaProxyAsync` | 2–3 days | 22c.1, 22c.2 |
| [22c.4](phase-22c.4-validation.md) | Validation and Edge Cases | 1–2 days | 22c.3 |

## Dependency Graph

```
Phase 22a + 22b.2 (done)
    │
    22c.1 (Manager key extension) ─┐
    22c.2 (Tunnel ALPN extension) ─┤
                                   ▼
                        22c.3 (Protocol routing)
                                   │
                                   ▼
                        22c.4 (Validation)
```

22c.1 and 22c.2 are independent and can be implemented in parallel. 22c.3 requires both. 22c.4 requires 22c.3.

## Files Impacted

| File | Change |
|------|--------|
| `Runtime/Transport/Http2/Http2ConnectionManager.cs` | Add proxy-tunneled overloads for `GetIfExists`, `GetOrCreateAsync`, `Remove`, `HasConnection`; add `BuildTunnelKey` private static helper |
| `Runtime/Transport/RawSocketTransport.cs` | Change `s_connectTunnelAlpnProtocols` to include `h2`; add BouncyCastle catch block in `EstablishConnectTunnelAsync`; restructure `DispatchViaProxyAsync` with HTTP/2 fast path, prepared-lease protocol routing, BouncyCastle inline retry, and `DispatchPreparedTunneledRequestAsync` helper |

### Files NOT Changed

| File | Why |
|------|-----|
| `Runtime/Transport/Http2/Http2Connection.cs` | Protocol-agnostic — operates on a `Stream` regardless of whether it wraps a direct socket or a tunnel |
| `Runtime/Transport/Http2/Http2Stream.cs` | Transport-agnostic; no tunnel awareness needed |
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | Pool manages TCP connections to hosts (proxy or origin) — does not know or care about tunneling |
| `Runtime/Core/` | No core type changes |

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| `\|via\|` separator in compound key | `\|` is not valid in RFC 952 hostnames — collision-free without struct key or extra allocation |
| Overloads instead of unified key type | Direct-connection methods are the hot path; adding a key builder on every direct request adds overhead for no benefit |
| BouncyCastle inline retry (not throw-and-delegate) | Users behind corporate proxies cannot easily retry manually; one-time cost before all subsequent connections use BouncyCastle |
| HTTP/1.1 stale retry through proxy out of scope | Existing gap unrelated to 22c; separate concern — 22c focuses on HTTP/2 only |
| No tunnel-establishment lock per origin×proxy | `Http2ConnectionManager._initLocks` already serializes creation; wasted CONNECT on the rare race is acceptable vs added complexity |

## Connection Pool Key Matrix

| Scenario | Pool Key | H2 Manager Key | TCP Socket Owner |
|----------|----------|-----------------|------------------|
| Direct HTTP/1.1 to `api:443` | `api:443:s` | (none) | ConnectionLease → ReturnToPool |
| Direct HTTP/2 to `api:443` | `api:443:s` | `api:443` | Http2Connection (TransferOwnership) |
| HTTP forward proxy to `api:80` | `proxy:8080:` | (none) | ConnectionLease → ReturnToPool |
| HTTPS tunnel HTTP/1.1 to `api:443` via `proxy:8080` | `proxy:8080:` | (none) | ConnectionLease → ReturnToPool |
| HTTPS tunnel HTTP/2 to `api:443` via `proxy:8080` | `proxy:8080:` | `api:443\|via\|proxy:8080` | Http2Connection (TransferOwnership) |

## Performance and Memory Targets

No new allocations on the fast path (reuse existing tunneled connection). Key string allocation only on first tunnel establishment (one-time cost amortized across all subsequent multiplexed requests). Dictionary lookup for `GetIfExists` on every proxy request through an existing tunnel.

## RFC Compliance

| RFC | Section | Feature | Sub-Phase |
|-----|---------|---------|-----------|
| RFC 9110 | 9.3.6 | CONNECT method semantics | 22c.2, 22c.3 |
| RFC 9113 | 3.2–3.3 | HTTP/2 connection preface and handshake | 22c.3 |
| RFC 7301 | 3 | ALPN protocol negotiation | 22c.2 |
| RFC 9113 | 9.1 | Connection management (GOAWAY) | 22c.4 |

## Deferred Items

1. **Concurrent tunnel establishment race window** — Between `GetIfExists` (null) and `GetOrCreateAsync` (acquires init lock), a second request may begin a second CONNECT. The init lock prevents two HTTP/2 connections; the second tunnel's TLS stream is disposed. Wasted CONNECT work is acceptable for now.
2. **Tunneled HTTP/2 idle timeout** — Idle tunneled connections persist until the proxy or origin closes them. General HTTP/2 idle connection management is a future phase.
3. **Maximum tunneled connections per proxy** — Unbounded tunnel growth for many origins through one proxy. Natural idle cleanup is sufficient for v1.
4. **HTTP/2 CONNECT (RFC 8441)** — Multiplexing CONNECT tunnels over one proxy HTTP/2 connection. Deferred.
5. **BouncyCastle ALPN through tunnel** — Validate on physical IL2CPP devices during 22c.4.

## Review Model

Both specialist agent reviews are mandatory per sub-phase:
- `.claude/agents/unity-infrastructure-architect.md`
- `.claude/agents/unity-network-architect.md`
