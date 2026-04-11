# Phase 22c Plan Review Fixes

**Date:** 2026-04-10  
**Phase:** 22c planning documentation follow-up  
**Status:** Plan corrected to match the current 22b.2 proxy/tunnel transport architecture

## What was updated

### Round 1 fixes

Revised the Phase 22c planning docs after initial review to remove assumptions that were true
before 22b.2 but no longer match the live transport structure.

1. Rebased 22c.2 on the actual tunnel setup call chain.
   - The plan now points at `AcquirePreparedProxyLeaseAsync(...)` / `RebindConnectTunnelLease(...)`
     as the `EstablishConnectTunnelAsync(...)` consumer path instead of treating
     `DispatchViaProxyAsync(...)` as the only caller.
   - This keeps the 22b.2 tunnel pool-key rebinding and HTTP/1.1 tunnel reuse model intact.

2. Corrected 22c.3 to preserve the existing lease-based HTTP/1.1 proxy path.
   - The plan no longer routes tunneled `http/1.1` fallback through `DispatchOnStreamAsync(...)`
     on a bare stream.
   - Instead, it now requires protocol routing on a prepared `ConnectionLease`, with the HTTP/1.1
     fallback staying on the existing lease-based proxy helper path so early-dispose,
     stale-retry, and same-origin tunnel reuse semantics from 22b.2 are preserved.

3. Corrected request sanitization for the HTTP/2 proxy path.
   - The plan now requires a sanitized tunneled request
     (`PrepareHttpsProxyTunnelRequest(...)` or equivalent shared-content clone) to be used for
     both the HTTP/2 fast path and the new-connection HTTP/2 path.
   - This closes the design gap where `Proxy-Authorization` could otherwise be forwarded to the
     origin over HTTP/2.

4. Aligned stale tunneled HTTP/2 retry rules with the direct path.
   - The plan now references `CanRetryH2RequestAfterTransportFailure(...)` instead of relying on
     `IsIdempotent()` alone, preserving the existing replayability guard for non-replayable bodies.

5. Relaxed the concurrent CONNECT expectation to match the accepted design.
   - The validation docs and test criteria now require exactly one retained `Http2Connection`
     under the origin×proxy key.
   - A transient second CONNECT/TLS attempt inside the accepted race window remains allowed as
     long as its stream/lease are disposed without leak.

### Round 2 fixes

Second review pass against the live code found four additional gaps:

1. **22c.2 — `TunnelResult` struct removed (was redundant).**
   - `EstablishConnectTunnelAsync` already returns `Task<TlsResult>`, not a bare `Stream`.
   - `TlsResult` already carries `SecureStream`, `NegotiatedAlpn`, `TlsVersion`, `ProviderName`.
   - `RebindConnectTunnelLease` already consumes all four fields and stores `NegotiatedAlpn` onto
     `lease.Connection.NegotiatedAlpnProtocol`. No new struct or return-type change is needed.
   - The actual 22c.2 work is: (1) change `s_connectTunnelAlpnProtocols` from `{ "http/1.1" }` to
     `{ "h2", "http/1.1" }`, and (2) add the BouncyCastle catch block with `MarkSslStreamViable()`.
   - Old Steps 1, 2, 5 (TunnelResult struct, return-type change, call-chain update) were no-ops
     and have been removed. Sub-phase rewritten from 5 steps to 2.

2. **22c.1 — `GetOrCreateCoreAsync` needs `host` and `port` parameters.**
   - `GetOrCreateSlowAsync` takes `(key, host, port, tlsStream, ct)` and uses `host`/`port` to
     construct `new Http2Connection(tlsStream, host, port, _options, _streamingOptions)`.
   - The plan's `GetOrCreateCoreAsync(key, tlsStream, ct)` was missing them; the extracted core
     method signature is now `GetOrCreateCoreAsync(key, host, port, tlsStream, ct)`.
   - For tunnel connections: `host` = `originHost`, `port` = `originPort` (not proxy coordinates).

3. **22c.1 — Missing `_disposed` guards in proxy overloads.**
   - The existing `GetIfExists`, `GetOrCreateAsync`, and `Remove` direct overloads all start with
     `Volatile.Read(ref _disposed)` guards. The proxy overloads now include the same guards.

4. **22c.3 — Stale-dispatch retry for HTTP/1.1 fallback clarified.**
   - `DispatchProxyHttp11WithRetryAsync` owns both lease acquisition and the stale-dispatch retry
     loop. When Stage 2 pre-acquires the lease, the HTTP/1.1 fallback in Stage 3 must still support
     stale-retry.
   - Plan updated: `DispatchPreparedTunneledRequestAsync` passes the pre-acquired lease as
     `preparedLease` to `DispatchProxyHttp11WithRetryAsync`, which uses it as the initial lease and
     re-acquires internally if the dispatch fails on a stale connection.
   - `CanRetryH2RequestAfterTransportFailure` corrected to use `request` (original) not
     `tunneledRequest` for consistency with the direct path.
   - `TransportProxyConnectHttp11Only` timeline event renamed to
     `TransportProxyConnectTunnelEstablished` since the tunnel can now negotiate either protocol.

## Files modified

- `Development/docs/phases/phase22c/phase-22c.1-manager-key-extension.md`
- `Development/docs/phases/phase22c/phase-22c.2-tunnel-alpn-extension.md`
- `Development/docs/phases/phase22c/phase-22c.3-protocol-routing.md`
- `Development/docs/phases/phase22c/overview.md`

## Decisions and trade-offs

1. Preserved the 22b.2 helper structure instead of redesigning the proxy transport in the plan.
   - Phase 22c is now explicitly additive over the current lease-based proxy path.
   - This reduces regression risk for CONNECT tunnel reuse and stale-retry semantics.

2. Kept request sanitization as a transport-level preparation step.
   - The sanitized tunneled request is prepared once and reused across the HTTP/2 fast path,
     HTTP/2 connection creation path, and HTTP/1.1 fallback path.
   - This avoids path-specific header-cleanup drift.

3. Kept the accepted concurrent CONNECT race as a documented trade-off.
   - The plan does not add a new establishment lock in 22c.
   - Validation now focuses on single retained connection ownership and leak-free disposal of any
     discarded second tunnel.

4. No new intermediate struct (`TunnelResult`) introduced.
   - Using `TlsResult` directly avoids unnecessary indirection and keeps the call chain consistent
     with every other TLS provider interaction in the transport layer.

### Round 3 fixes (specialist agent reviews + BouncyCastle policy clarification)

Two specialist agents (unity-infrastructure-architect and unity-network-architect) reviewed the
round-2 plan and found six additional actionable issues. Simultaneously, the BouncyCastle fallback
design was corrected per project policy.

5. **BouncyCastle policy corrected — no runtime fallback.**
   - BouncyCastle is used only when SslStream is genuinely unavailable (stripped from IL2CPP build).
     `TlsProviderSelector.GetProvider` selects the right provider before the handshake. TLS
     failures through the tunnel propagate as-is; there is no catch-and-retry path.
   - Removed: `EstablishConnectTunnelAsync` BouncyCastle catch block (22c.2 Step 2).
   - Removed: BouncyCastle inline retry catch in `DispatchViaProxyAsync` Stage 2 (22c.3 Step 3).
   - Removed: `TransportProxyTlsFallbackRetry` timeline event (22c.3 Step 6).
   - Removed: `IsSslStreamKnownViable()` guard and new method (no longer needed).
   - Updated: overview.md Core Design Principle 6.

6. **[Critical] `tlsStream` leak on `WaitAsync` cancellation in `GetOrCreateSlowAsync`.**
   - If `initLock.WaitAsync(ct)` throws `OperationCanceledException` before the lock body,
     `tlsStream` is passed in but not owned by any `Http2Connection`. It is never disposed.
     More severe for the proxy overload because `TransferOwnership()` already released the
     semaphore — the proxy TCP socket has no pool fallback.
   - Fix: `GetOrCreateSlowAsync` must wrap `tlsStream` in a guarded path that disposes it on any
     exception exit before `Http2Connection` is constructed. Added detailed note in 22c.1 Step 6.

7. **[High] `GetOrCreateAsync` proxy overload missing fast-path `TryGetValue` check.**
   - Without it, every concurrent-reuse call (past `GetIfExists`) allocates a `Task` state machine
     and acquires a `SemaphoreSlim` unnecessarily. Added the fast-path to 22c.1 Step 3, mirroring
     the existing direct overload.

8. **[High] `lease = null` after ownership transfer — missing from 22c.3.**
   - After `lease.TransferOwnership()` + `GetOrCreateAsync`, the outer `finally { lease?.Dispose(); }`
     in `DispatchViaProxyAsync` would call `ReleaseSemaphoreOnce()` a second time — releasing two
     permits for one connection. The `lease = null` pattern (used in the direct path at line 331)
     must be applied here too. Added to 22c.3 Step 4, Step 5 outline, and completion criteria.

9. **[High] `Http2Connection.Dispose()` stream ownership unverified.**
   - `Http2Connection.Dispose()` must call `Dispose()` on its underlying stream for the proxy
     socket disposal chain to work. Added as an explicit prerequisite check before 22c.3 Step 4
     is coded.

10. **[Medium] Stage 1 stale-catch too broad.**
    - `catch (Exception) when (!ct.IsCancellationRequested)` catches H2 protocol errors and
      application-level errors as if they were stale-connection transport failures. Narrowed to
      `IOException || (UHttpException && IsRetryable())` in 22c.3 Step 2.

11. **[Medium] `tunneledRequest` allocation before fast-path check contradicts no-alloc goal.**
    - `PrepareHttpsProxyTunnelRequest(request)` was unconditionally allocated in Step 1 even on
      the steady-state fast path. Deferred to a lazy build inside the fast-path block (when an
      alive connection is found) and to just before Stage 3 on the new-tunnel path.

12. **[Medium] `NegotiatedAlpnProtocol == null` routing to HTTP/1.1 not documented.**
    - RFC 7301 §3.2: null ALPN (server sent no extension) means HTTP/1.1 default. Documented in
      22c.3 Step 4.

## Files modified (rounds 1–3)

- `Development/docs/phases/phase22c/phase-22c.1-manager-key-extension.md`
- `Development/docs/phases/phase22c/phase-22c.2-tunnel-alpn-extension.md`
- `Development/docs/phases/phase22c/phase-22c.3-protocol-routing.md`
- `Development/docs/phases/phase22c/overview.md`

## Decisions and trade-offs

1. Preserved the 22b.2 helper structure instead of redesigning the proxy transport in the plan.
2. Kept request sanitization as a transport-level preparation step.
3. Kept the accepted concurrent CONNECT race as a documented trade-off.
4. No new intermediate struct (`TunnelResult`) introduced.
5. BouncyCastle remains pre-handshake selection only — no runtime fallback in the tunnel path or
   anywhere else. This is a project-wide policy.

## Deferred / still required

1. Physical-device IL2CPP validation for ALPN through CONNECT tunnels remains open.
2. Any future decision to eliminate the accepted duplicate-CONNECT race remains deferred beyond 22c.
