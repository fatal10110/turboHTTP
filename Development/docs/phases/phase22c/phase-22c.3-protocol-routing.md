# Phase 22c.3: Protocol Routing in `DispatchViaProxyAsync`

**Depends on:** 22c.1 (manager key extension), 22c.2 (tunnel ALPN extension)
**Assemblies:** `TurboHTTP.Transport`
**Files modified:** 1

---

## Context

`DispatchViaProxyAsync` in `RawSocketTransport.cs` currently:
1. Parses proxy coordinates and selects between forward-proxy vs CONNECT-tunnel flow
2. Delegates the actual proxy lease/tunnel work to the 22b.2 helpers
   (`DispatchProxyHttp11WithRetryAsync` and `AcquirePreparedProxyLeaseAsync`)
3. Reuses tunneled HTTP/1.1 leases correctly, but still treats every prepared tunnel as an
   HTTP/1.1 connection even when ALPN could negotiate `h2`

With 22c.1 and 22c.2 complete, this sub-phase restructures `DispatchViaProxyAsync` into three stages:

1. **Fast path** — check for an existing tunneled HTTP/2 connection before establishing any CONNECT tunnel
2. **CONNECT tunnel establishment** — keep using `AcquirePreparedProxyLeaseAsync(...)` so tunnel
   pool-key rebinding and HTTP/1.1 reuse semantics from 22b.2 remain intact
3. **Protocol routing** — route a prepared tunneled lease to HTTP/2
   (`Http2ConnectionManager`) or the existing lease-based HTTP/1.1 proxy dispatch path based on
   `lease.Connection.NegotiatedAlpnProtocol`

Plus the BouncyCastle inline retry path when SslStream fails through the tunnel.

---

## Step 1: Parse Proxy Coordinates

At the top of `DispatchViaProxyAsync`, extract proxy host and port before the stages. These are
needed for both the fast path (stage 1) and tunnel establishment (stage 2).

`tunneledRequest` is **not** built here. It must be deferred until the fast-path check to avoid a
`PrepareHttpsProxyTunnelRequest` header-clone allocation on every steady-state request. Build it
lazily just before each use site instead (see Stages 1 and 3):

```csharp
private async Task DispatchViaProxyAsync(
    UHttpRequest request,
    IHttpHandler handler,
    RequestContext context,
    string host,
    int port,
    bool secure,
    ProxySettings proxy,
    CancellationToken ct)
{
    if (proxy?.Address == null)
        throw new UHttpException(new UHttpError(
            UHttpErrorType.ProxyError, "Proxy address is null."));

    var proxyHost = proxy.Address.Host;
    var proxyPort = proxy.Address.IsDefaultPort ? 80 : proxy.Address.Port;

    // tunneledRequest is built lazily below to avoid a header-clone allocation
    // on the fast path when an alive tunnel connection already exists.

    // Stage 1, 2, 3 follow...
}
```

---

## Step 2: Stage 1 — HTTP/2 Fast Path (HTTPS Proxying Only)

Before establishing any CONNECT tunnel, check if an alive tunneled HTTP/2 connection already exists for this origin through this proxy. Only applies to HTTPS (`secure == true`); HTTP forward proxy requests always use HTTP/1.1.

```csharp
// Stage 1: HTTP/2 fast path — check for existing tunneled connection
if (secure)
{
    var existingH2 = _h2Manager.GetIfExists(host, port, proxyHost, proxyPort);
    if (existingH2 != null)
    {
        // Build tunneledRequest lazily — only when an alive connection exists.
        var tunneledRequest = PrepareHttpsProxyTunnelRequest(request);
        try
        {
            context.RecordEvent("TransportProxyH2Reuse");
            await existingH2.DispatchAsync(tunneledRequest, handler, context, ct)
                .ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (
            !ct.IsCancellationRequested
            && (ex is IOException || (ex is UHttpException uhe && uhe.Error.IsRetryable())))
        {
            _h2Manager.Remove(host, port, proxyHost, proxyPort);
            if (!CanRetryH2RequestAfterTransportFailure(request))
                throw;
            context.RecordEvent("TransportProxyH2StaleRetry");
            // Fall through to establish a new CONNECT tunnel
        }
    }
}
```

**Stale retry semantics:** Use `request` (the original) for `CanRetryH2RequestAfterTransportFailure`,
consistent with the direct path. Both `request` and `tunneledRequest` share the same `Content`
reference (via `CopyWithSharedContent()`), so the result is identical — the convention is for
consistency.

**Narrow exception filter:** The catch must be narrowed to transport-level exceptions
(`IOException`, retryable `UHttpException`) rather than `catch (Exception)`. An unfiltered catch
would treat H2 protocol errors (`Http2StreamError`, `RST_STREAM`) and application-level errors as
stale-connection signals and attempt a full tunnel re-establishment — incorrect behavior. The
`IsRetryable()` check on `UHttpException` matches network/timeout errors but not protocol or
request-validation errors.

---

## Step 3: Stage 2 — Acquire Proxy Connection Lease and Establish Tunnel

This stage should continue to use the 22b.2 prepared-lease helpers instead of switching back to
raw-stream dispatch. The secure proxy slow path needs a prepared lease whose transport binding has
already been rebound to the tunneled target and whose `NegotiatedAlpnProtocol` now reflects the
tunnel TLS handshake.

```csharp
if (!secure)
{
    // HTTP forward proxy — unchanged, still owned by the existing lease-based helper
    var forwardedRequest = PrepareHttpProxyForwardRequest(request, proxy);
    await DispatchProxyHttp11WithRetryAsync(
            proxyHost,
            proxyPort,
            poolKeyOverride: null,
            establishTunnel: false,
            targetHost: null,
            targetPort: 0,
            proxy,
            forwardedRequest,
            request,
            handler,
            context,
            ct)
        .ConfigureAwait(false);
    return;
}

var tunnelPoolKey = BuildConnectTunnelPoolKey(proxyHost, proxyPort, host, port);
ConnectionLease lease = null;
try
{
    lease = await AcquirePreparedProxyLeaseAsync(
            proxyHost,
            proxyPort,
            tunnelPoolKey,
            establishTunnel: true,
            host,
            port,
            proxy,
            context,
            ct)
        .ConfigureAwait(false);
}
catch
{
    // AcquirePreparedProxyLeaseAsync disposes the lease on failure before rethrowing.
    // TLS failures propagate as-is — BouncyCastle is selected automatically by
    // TlsProviderSelector.GetProvider before the handshake if SslStream is unavailable.
    throw;
}
```

**No BouncyCastle inline retry here.** The BouncyCastle policy is: BouncyCastle is used only when
SslStream is genuinely unavailable (stripped from the build). `TlsProviderSelector.GetProvider`
returns the correct provider before the TLS handshake begins. A TLS failure through the tunnel
propagates to the caller unchanged — it is not an opportunity to switch providers.

---

## Step 4: Stage 3 — Protocol Routing After Tunnel TLS

**Prerequisite before coding this step:** Read `Http2Connection.Dispose()` in
`Runtime/Transport/Http2/Http2Connection.cs` and confirm it calls `Dispose()` on its underlying
`_stream`. After `lease.TransferOwnership()`, the proxy TCP socket is not pool-managed — if
`Http2Connection.Dispose()` does not close the stream, the socket leaks permanently. This check
must be done before any Stage 3 code is written.

Extract protocol routing into a private helper that operates on a prepared `ConnectionLease`.
Do not fall back to `DispatchOnStreamAsync(...)` on a bare stream; the HTTP/1.1 fallback must
stay on the existing lease-based proxy path so 22b.2 early-dispose, stale-retry, and same-origin
tunnel reuse semantics are preserved.

**`NegotiatedAlpnProtocol == null`:** If the server sent no ALPN extension,
`TlsResult.NegotiatedAlpn` is `null` (documented in `TlsResult.cs`), so
`lease.Connection.NegotiatedAlpnProtocol` is `null`. `null != "h2"`, so the HTTP/1.1 fallback
is used. This is correct per RFC 7301 §3.2 — absence of ALPN means the default application
protocol for the context (HTTP/1.1 here).

```csharp
private async Task DispatchPreparedTunneledRequestAsync(
    ConnectionLease lease,    // prepared proxy lease rebound to the tunneled target
    UHttpRequest tunneledRequest,
    UHttpRequest originalRequest,
    IHttpHandler handler,
    RequestContext context,
    string originHost,
    int originPort,
    string proxyHost,
    int proxyPort,
    string tunnelPoolKey,
    ProxySettings proxy,
    CancellationToken ct)
{
    if (lease.Connection.NegotiatedAlpnProtocol == "h2")
    {
        context.RecordEvent("TransportProxyH2Init");
        lease.TransferOwnership();

        var h2Conn = await _h2Manager.GetOrCreateAsync(
            originHost, originPort,
            proxyHost, proxyPort,
            lease.Connection.Stream, ct).ConfigureAwait(false);

        // lease = null: see ownership note below. The caller must null its lease
        // reference after this method returns so the outer finally does not
        // double-release the semaphore.

        await h2Conn.DispatchAsync(tunneledRequest, handler, context, ct).ConfigureAwait(false);
        return;
    }

    // HTTP/1.1 fallback through tunnel — keep the existing lease-based proxy semantics.
    // DispatchProxyHttp11WithRetryAsync owns the stale-dispatch retry loop; pass the
    // pre-acquired lease as the initial lease so it can re-acquire if the dispatch fails
    // on a stale connection. See the note below on stale-retry ownership.
    context.RecordEvent("TransportProxyH1Dispatch");
    await DispatchProxyHttp11WithRetryAsync(
            proxyHost,
            proxyPort,
            tunnelPoolKey,
            establishTunnel: true,
            originHost,
            originPort,
            proxy,
            tunneledRequest,
            originalRequest,
            handler,
            context,
            ct,
            preparedLease: lease)
        .ConfigureAwait(false);
}
```

**Stale-dispatch retry for HTTP/1.1 fallback:** `DispatchProxyHttp11WithRetryAsync` currently
owns the full acquire → dispatch → on-stale-failure re-acquire → re-dispatch loop. When Stage 3
routes to HTTP/1.1, it passes the already-acquired lease via an optional `preparedLease` parameter
so `DispatchProxyHttp11WithRetryAsync` can use it as the initial lease and still re-acquire
internally if the dispatch fails. Read the current `DispatchProxyHttp11WithRetryAsync` body before
coding; the cleanest approach is to add a `ConnectionLease preparedLease = null` overload parameter
that skips the first `AcquirePreparedProxyLeaseAsync` call when non-null.

**`lease.TransferOwnership()` and `lease = null`:** `TransferOwnership()` releases the per-proxy
semaphore permit (sets `_released = true`, calls `ReleaseSemaphoreOnce()`). The `Http2Connection`
takes ownership of the TLS stream (which wraps the proxy tunnel stream, which wraps the proxy TCP
socket). When the HTTP/2 connection is disposed, the disposal chain is:
`Http2Connection.Dispose()` → `TLS stream.Dispose()` → `tunnel stream.Dispose()` →
`proxy socket.Dispose()`.

After `TransferOwnership()`, the caller in `DispatchViaProxyAsync` must set `lease = null`
immediately after the `DispatchPreparedTunneledRequestAsync` call returns (on the HTTP/2 path).
This mirrors the existing direct-connection pattern at lines 328–331 of `RawSocketTransport.cs`:

```csharp
lease.TransferOwnership();
var h2Conn = await _h2Manager.GetOrCreateAsync(...);
lease = null;   // ← prevents outer finally { lease?.Dispose(); } from double-releasing semaphore
```

Without `lease = null`, if the outer `try/finally` in `DispatchViaProxyAsync` calls
`lease.Dispose()` after `TransferOwnership()` has already fired, `ReleaseSemaphoreOnce()` is
called a second time — releasing two permits for one connection and allowing one extra concurrent
connection above the configured per-host limit.

**`PrepareHttpsProxyTunnelRequest`:** Existing method — strips proxy-specific headers (for
example `Proxy-Authorization`) before sending to the origin. The prepared tunneled request must
be used on both the HTTP/2 fast path and the new-connection HTTP/2 path, not only on the
HTTP/1.1 fallback path.

**`TransportProxyConnectHttp11Only` timeline event:** `AcquirePreparedProxyLeaseAsync` currently
emits this event when establishing a new CONNECT tunnel. After 22c.3 the tunnel can negotiate h2,
so the label is misleading. Replace it with `TransportProxyConnectTunnelEstablished` in this
sub-phase to accurately reflect the generic tunnel establishment regardless of protocol.

---

## Step 5: Restructure `DispatchViaProxyAsync` (Full Method)

After Steps 1–4, the full method body looks like:

```
1.  Validate proxy coordinates, extract proxyHost/proxyPort
2.  if (secure): HTTP/2 fast path with stale retry [Stage 1]
    a. GetIfExists(host, port, proxyHost, proxyPort)
    b. If alive: PrepareHttpsProxyTunnelRequest(request) → DispatchAsync → return
    c. On transport exception, Remove + check retryability → fall through
3.  if (!secure): HTTP forward proxy dispatch via the existing helper and return
4.  [Stage 2] Build tunnelPoolKey; acquire prepared proxy lease (BouncyCastle retry if TlsError)
5.  Build tunneledRequest = PrepareHttpsProxyTunnelRequest(request)  ← first use on new-tunnel path
6.  Call DispatchPreparedTunneledRequestAsync(lease, ...)
7.  lease = null                 ← MUST be set after the call returns on the HTTP/2 path;
                                    on the HTTP/1.1 path, DispatchProxyHttp11WithRetryAsync
                                    owns the lease and the caller must also null the reference
8.  Outer finally { lease?.Dispose(); }  ← lease is null here on the success paths above;
                                           only fires on exception or unexpected exits
```

**`lease = null` is not optional.** Both the HTTP/2 path (after `TransferOwnership()` +
`GetOrCreateAsync`) and the HTTP/1.1 path (after handing the lease to
`DispatchProxyHttp11WithRetryAsync`) must null the outer `lease` variable. The outer
`finally { lease?.Dispose(); }` is the safety net for exception paths — it must not run on the
success paths where ownership has been transferred.

Maintain the existing 22b.2 behavior for all paths that do not involve HTTP/2 (`!secure` path
and `NegotiatedAlpn == null`/`"http/1.1"` path) to avoid regressing proxy stale-retry,
early-dispose, and same-origin tunnel reuse semantics.

---

## Step 6: Timeline Events

New `RecordEvent` calls added by this sub-phase:

| Event | When |
|-------|------|
| `TransportProxyH2Reuse` | Stage 1: existing tunneled HTTP/2 connection found |
| `TransportProxyH2StaleRetry` | Stage 1: stale connection detected, falling through to new tunnel |
| `TransportProxyConnectTunnelEstablished` | Stage 2: CONNECT tunnel established (replaces `TransportProxyConnectHttp11Only`) |
| `TransportProxyH2Init` | Stage 3: new HTTP/2 connection created through tunnel |
| `TransportProxyH1Dispatch` | Stage 3: ALPN negotiated HTTP/1.1 (or null — no ALPN extension) |

These parallel the existing direct-connection events (`TransportH2Reuse`, `TransportH2StaleRetry`, `TransportH2Init`). `TransportProxyTlsFallbackRetry` is removed — BouncyCastle is not a runtime fallback.

`TransportProxyConnectHttp11Only` (currently emitted in `AcquirePreparedProxyLeaseAsync`) must be
replaced by `TransportProxyConnectTunnelEstablished` as part of this sub-phase since the tunnel
can now negotiate either h2 or http/1.1.

---

## Completion Criteria

- [ ] `DispatchViaProxyAsync` restructured into three stages (fast path → tunnel → routing)
- [ ] Stage 1: `GetIfExists(host, port, proxyHost, proxyPort)` checked before any CONNECT for HTTPS requests
- [ ] Stage 1 uses the sanitized tunneled request (`PrepareHttpsProxyTunnelRequest` or equivalent shared-content clone) for all HTTP/2 dispatches
- [ ] Stage 1 stale retry uses `CanRetryH2RequestAfterTransportFailure(...)`, matching the direct path's replayability rules
- [ ] Stage 3 HTTP/2 path: `lease.TransferOwnership()` called, `GetOrCreateAsync(origin, port, proxy, pport, stream, ct)` used, `h2Conn.DispatchAsync` invoked, `lease = null` set in caller after return
- [ ] Stage 3 HTTP/1.1 path stays on the existing lease-based proxy/tunnel dispatch helper (no raw-stream fallback); caller sets `lease = null` after handoff
- [ ] Outer `finally { lease?.Dispose(); }` present in `DispatchViaProxyAsync` as exception-path safety net; `lease` is null on all normal-exit paths after ownership transfer
- [ ] No BouncyCastle inline retry in `DispatchViaProxyAsync` — TLS failures propagate; BouncyCastle is selected by `TlsProviderSelector.GetProvider` before the handshake if SslStream is unavailable
- [ ] HTTP forward proxy path (`!secure`): unchanged — still flows through the existing lease-based helper
- [ ] All timeline events recorded on their respective paths (including `TransportProxyConnectTunnelEstablished` replacing `TransportProxyConnectHttp11Only`)
- [ ] `TransportProxyConnectHttp11Only` removed from `AcquirePreparedProxyLeaseAsync`
- [ ] No regression on existing proxy tests

### Unit Tests

Tests go in `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs` or `Http1/ProxyTunnelTests.cs`.

- [ ] **First request, ALPN h2:** CONNECT + TLS + `GetOrCreateAsync(proxy overload)` → HTTP/2 connection created under tunnel key using the sanitized tunneled request
- [ ] **Second request, existing tunneled h2:** `GetIfExists(proxy overload)` returns connection → `DispatchAsync` called, no CONNECT
- [ ] **Different proxy, same origin:** Separate CONNECT → separate HTTP/2 connection under different tunnel key
- [ ] **Different origin, same proxy:** Separate CONNECT → separate HTTP/2 connection
- [ ] **Direct request, same origin (no proxy):** Uses direct key, no interaction with tunnel key
- [ ] **Stale tunneled h2, replayable request:** `DispatchAsync` throws → `Remove(proxy overload)` → new tunnel established
- [ ] **Stale tunneled h2, non-replayable request:** exception propagated, no retry
- [ ] **ALPN negotiates http/1.1:** existing lease-based HTTP/1.1 proxy dispatch path is used, no `GetOrCreateAsync`, no `TransferOwnership`
- [ ] **BouncyCastle inline retry:** First prepared-tunnel acquisition throws `TlsError` → failed lease already disposed by the helper → fresh prepared tunnel acquired → routing continues
- [ ] **`TransferOwnership` releases proxy semaphore:** Verify permit count after Stage 3 HTTP/2 path
- [ ] **Concurrent requests, same origin, same proxy:** exactly one `Http2Connection` retained under the tunnel key; a second transient CONNECT/TLS attempt is acceptable if its stream/lease are disposed without leak
- [ ] **Concurrent requests, different origins, same proxy:** Two separate tunnels established independently
- [ ] **Integration test:** Full round-trip through a mock CONNECT proxy server with HTTP/2 ALPN negotiation (mock server advertises `h2` in ALPN; verify request dispatched over HTTP/2 frames)

---

## Notes for Implementation

- Read the full current body of `DispatchViaProxyAsync` and `EstablishConnectTunnelAsync` before making changes.
- Read `ConnectionLease.TransferOwnership()` to confirm it releases the semaphore and removes the connection from pool management.
- The 22b.2 proxy helper chain already owns lease disposal on failure. Keep that ownership model rather than reintroducing a second `using var lease` / manual-dispose path in `DispatchViaProxyAsync`.
- Do not introduce new `async` state machines unless necessary — if `DispatchOnTunnelResultAsync` is a hot path helper, keep it as a regular `async Task` method (same as the existing pattern).
- After the refactor, run all existing proxy tests to confirm no regression before writing new tests.
