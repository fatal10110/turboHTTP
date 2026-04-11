# Phase 22c: HTTP/2 Through CONNECT Proxy Tunnels

**Milestone:** M4 (v2.0 follow-up)
**Dependencies:** Phase 22a (end-to-end streaming), Phase 22b.2 (streaming through proxy connections)
**Estimated Complexity:** High
**Critical:** No — HTTP/2 through proxies is a performance optimization, not a correctness fix. HTTP/1.1 through CONNECT tunnels works correctly today.
**Compatibility:** Additive. No breaking changes. Direct HTTP/2 connections are unaffected.

## Overview

When TurboHTTP connects to an HTTPS origin through a CONNECT proxy, the following happens today:

```
Client ──[HTTP/1.1 CONNECT]──> Proxy ──[TCP tunnel]──> Origin Server
                                 │
                         tunnel established
                                 │
Client ──[TLS handshake, ALPN: http/1.1 only]────────> Origin Server
                                 │
Client ──[HTTP/1.1 framing]───────────────────────────> Origin Server
```

The TLS handshake through the tunnel (`EstablishConnectTunnelAsync` at `RawSocketTransport.cs:514–518`) hardcodes `new[] { "http/1.1" }` for ALPN, deliberately suppressing HTTP/2 negotiation:

```csharp
// CONNECT tunnels currently run request framing through the HTTP/1.1 parser path only.
// Advertising h2 ALPN here would negotiate HTTP/2 that this tunnel code path cannot
// yet service safely.
var tlsResult = await tlsProvider.WrapAsync(
    proxyStream,
    targetHost,
    new[] { "http/1.1" },
    ct).ConfigureAwait(false);
return tlsResult.SecureStream;
```

After the TLS handshake, the secure proxy path remains inside the 22b.2 lease-based HTTP/1.1
helpers (`DispatchProxyHttp11WithRetryAsync` / `AcquirePreparedProxyLeaseAsync`). There is no
ALPN check, no `Http2ConnectionManager` integration, and no protocol routing. This means:

1. **No HTTP/2 multiplexing through proxies.** Each request to the same origin through the same proxy requires a separate CONNECT tunnel and TCP connection. For APIs that serve many concurrent requests to one origin (dashboards, real-time feeds, asset loading), this is a significant performance gap.
2. **No HTTP/2 server push through proxies.** (Server push is not implemented in TurboHTTP, but the tunnel limitation would block it if added later.)
3. **Higher latency per request.** Each proxy request pays the full TCP + CONNECT + TLS cost independently instead of amortizing it across multiplexed streams.

Phase 22c enables HTTP/2 ALPN negotiation through CONNECT tunnels, adds protocol routing to the proxy dispatch path, and extends `Http2ConnectionManager` to track proxy-tunneled HTTP/2 connections separately from direct ones.

---

## Goals

1. Enable HTTP/2 ALPN negotiation (`h2` + `http/1.1`) on TLS handshakes through CONNECT tunnels.
2. Route proxy-tunneled connections to `Http2ConnectionManager` when ALPN negotiates `h2`.
3. Support HTTP/2 multiplexing through a single CONNECT tunnel — multiple concurrent requests to the same origin reuse one tunnel + one HTTP/2 connection.
4. Correctly manage the lifecycle of proxy-tunneled HTTP/2 connections — the connection key must distinguish direct vs proxy paths and different proxies.
5. Maintain correct connection pool semantics — proxy connection leases, semaphore permits, and idle eviction must all work correctly for tunneled HTTP/2.
6. Preserve HTTP/1.1 fallback when the origin does not support HTTP/2 (ALPN negotiates `http/1.1`).

## Non-Goals

1. **HTTP/2 on the tunnel itself (client ↔ proxy leg).** The CONNECT handshake between client and proxy remains HTTP/1.1. HTTP/2 CONNECT (RFC 8441, extended CONNECT) is a separate, more complex protocol that requires the proxy itself to support HTTP/2. This is deferred.
2. **HTTP/2 for HTTP (non-HTTPS) forward proxy requests.** HTTP/2 requires TLS (h2). Plain HTTP forward proxy requests (`GET http://example.com/path HTTP/1.1`) remain HTTP/1.1. (h2c / HTTP/2 cleartext is not supported by TurboHTTP.)
3. **Proxy connection pooling at the HTTP/2 level.** The proxy TCP connection is managed by `TcpConnectionPool`. The HTTP/2 connection through the tunnel is managed by `Http2ConnectionManager`. These remain separate — no unified "proxy-aware HTTP/2 connection pool."
4. **SOCKS proxy support.** Only HTTP/HTTPS CONNECT proxy tunneling is in scope.
5. **Multiple origins through one proxy connection.** Each CONNECT tunnel is origin-specific. Multiplexing different origins through one proxy TCP connection would require HTTP/2 on the client ↔ proxy leg (Non-Goal #1).

---

## Why This Is Not Trivial

The direct connection path (`DispatchCoreAsync` lines 188–232) has protocol routing built into its main flow:

```csharp
// 4a. HTTP/2 fast path — reuse existing h2 connection
var h2Conn = _h2Manager.GetIfExists(host, port);
if (h2Conn != null) { ... h2Conn.DispatchAsync(...) ... }

// 4b. Get connection lease from pool
using var lease = await _pool.GetConnectionAsync(host, port, secure, ct);

// 4c. Protocol routing based on ALPN
if (lease.Connection.NegotiatedAlpnProtocol == "h2") {
    lease.TransferOwnership();
    var h2Conn = await _h2Manager.GetOrCreateAsync(host, port, lease.Connection.Stream, ct);
    await h2Conn.DispatchAsync(request, handler, context, ct);
    return;
}

// 4d. HTTP/1.1 path
```

The proxy path still bypasses the direct HTTP/2 routing flow. Since 22b.2 it does so through a
prepared-lease helper path rather than a raw-stream dispatch. To enable HTTP/2 through tunnels,
we need to:

1. **Change the ALPN list** from `{ "http/1.1" }` to `{ "h2", "http/1.1" }` in the tunnel TLS handshake.
2. **Add protocol routing** after the tunnel TLS handshake — check `NegotiatedAlpn` and route to `Http2ConnectionManager` when `h2`.
3. **Change the `Http2ConnectionManager` key** — the current key is `$"{host}:{port}"` (origin only). For tunneled connections, the key must include the proxy identity, because a tunneled HTTP/2 connection through Proxy A cannot be reused for requests through Proxy B (they are different TCP connections).
4. **Handle connection lease ownership** — the proxy connection lease (`using var lease = await _pool.GetConnectionAsync(proxyHost, proxyPort, ...)`) wraps the TCP connection to the proxy. When an HTTP/2 connection is created through the tunnel, ownership must be transferred to the `Http2Connection`. The `Http2Connection` must keep the proxy connection alive for its entire multiplexed lifetime, not just one request.
5. **Handle connection reuse** — when a second request arrives for the same origin through the same proxy, it must find the existing tunneled HTTP/2 connection via `Http2ConnectionManager` and skip the CONNECT handshake entirely. This is the fast path.
6. **Handle connection death** — when a tunneled HTTP/2 connection dies (GOAWAY, network error, idle timeout), both the HTTP/2 connection and the underlying proxy tunnel must be cleaned up.

---

## Core Design Principles

### 1. Key Scheme: Origin × Proxy

The `Http2ConnectionManager` currently uses `$"{host}:{port}"` as the connection key. This works for direct connections where each key corresponds to one unique TCP + TLS connection.

For proxy-tunneled connections, the same origin can be reached through different proxies. Each proxy path is a different physical connection. The key must therefore include the proxy identity:

```
Direct:  "api.example.com:443"
Proxy A: "api.example.com:443|via|proxy-a.corp.com:8080"
Proxy B: "api.example.com:443|via|proxy-b.corp.com:3128"
```

This ensures:
- Requests to `api.example.com` through Proxy A reuse the Proxy A tunnel
- Requests to `api.example.com` through Proxy B reuse the Proxy B tunnel
- Direct requests to `api.example.com` (no proxy) use the direct connection
- All three are independent in `Http2ConnectionManager`

#### Key Format

```csharp
// Direct connection (unchanged):
string key = $"{host}:{port}";

// Proxy-tunneled connection:
string key = $"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}";
```

The `|via|` separator is chosen because `|` is not valid in hostnames (RFC 952) and will never appear in host:port strings, avoiding ambiguity.

### 2. Connection Lease Lifetime Extension

For direct connections, the flow is:

```
lease = pool.GetConnectionAsync(host, port, secure)
lease.TransferOwnership()  // semaphore released, connection given to Http2ConnectionManager
// Http2Connection now owns the stream
```

For proxy-tunneled connections:

```
proxyLease = pool.GetConnectionAsync(proxyHost, proxyPort, false)
// Establish CONNECT tunnel on proxyLease.Connection.Stream
// TLS handshake through tunnel → tlsStream
// ALPN = h2
proxyLease.TransferOwnership()  // semaphore released, proxy connection given to Http2Connection
// Http2Connection now owns the TLS stream (which wraps the proxy tunnel stream)
```

The proxy `PooledConnection` hosts the underlying TCP socket to the proxy. The TLS stream wraps this socket through the tunnel. `Http2Connection` owns the TLS stream. When the HTTP/2 connection is disposed, the TLS stream is disposed, which disposes the inner proxy stream, which disposes the socket.

**Critical:** `TransferOwnership()` on the proxy lease releases the per-host semaphore for the *proxy* host. This is correct — the semaphore tracks concurrent connections to the proxy, and the HTTP/2 connection holds one proxy connection for its entire multiplexed lifetime. If the proxy has `maxConnectionsPerHost = 6`, one HTTP/2 tunnel consumes one of those six permits for the duration of the connection.

### 3. Fast Path: Reuse Existing Tunneled HTTP/2 Connection

When a second request arrives for the same origin through the same proxy:

1. `DispatchCoreAsync` detects `proxy != null` and calls `DispatchViaProxyAsync`
2. Before establishing a new CONNECT tunnel, check `_h2Manager.GetIfExists(originHost, originPort, proxyHost, proxyPort)` using the extended key
3. If an alive HTTP/2 connection exists → dispatch directly, skip CONNECT
4. If no alive connection → establish CONNECT tunnel, negotiate TLS+ALPN, create new HTTP/2 connection

This fast path is the entire point of HTTP/2 through proxies — subsequent requests skip DNS, TCP, CONNECT, and TLS entirely and go straight to HTTP/2 stream multiplexing.

### 4. Stale Connection Retry Through Proxy

The direct path has retry-on-stale logic for dead HTTP/2 connections (lines 201–214). The proxy path needs the same:

```csharp
var h2Conn = _h2Manager.GetIfExists(tunnelKey);
if (h2Conn != null)
{
    try
    {
        await h2Conn.DispatchAsync(tunneledRequest, handler, context, ct);
        return;
    }
    catch (Exception) when (!ct.IsCancellationRequested)
    {
        _h2Manager.Remove(tunnelKey);
        if (!CanRetryH2RequestAfterTransportFailure(tunneledRequest))
            throw;
        // Fall through to establish a new CONNECT tunnel
    }
}
```

---

## Detailed Design

### `Http2ConnectionManager` Changes

#### Extended Key Methods

The current public API uses `(string host, int port)` for all operations:
- `GetIfExists(string host, int port)`
- `GetOrCreateAsync(string host, int port, Stream tlsStream, CancellationToken ct)`
- `Remove(string host, int port)`
- `HasConnection(string host, int port)`

These methods continue to work unchanged for direct connections (backward compatible). New overloads are added for proxy-tunneled connections:

```csharp
/// <summary>
/// Get an existing alive h2 connection through a proxy tunnel, or null.
/// </summary>
public Http2Connection GetIfExists(string originHost, int originPort, string proxyHost, int proxyPort)
{
    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
    if (_connections.TryGetValue(key, out var conn) && conn.IsAlive)
        return conn;
    return null;
}

/// <summary>
/// Get or create an h2 connection through a proxy tunnel.
/// </summary>
public ValueTask<Http2Connection> GetOrCreateAsync(
    string originHost, int originPort,
    string proxyHost, int proxyPort,
    Stream tlsStream, CancellationToken ct)
{
    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
    // Same logic as existing GetOrCreateAsync, using the tunnel key
    ...
}

/// <summary>
/// Remove and dispose a stale proxy-tunneled connection.
/// </summary>
public void Remove(string originHost, int originPort, string proxyHost, int proxyPort)
{
    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
    if (_connections.TryRemove(key, out var conn))
        conn.Dispose();
}

private static string BuildTunnelKey(string originHost, int originPort, string proxyHost, int proxyPort)
{
    return $"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}";
}
```

**Why overloads instead of a unified key type:** The two-parameter methods are called on every direct request (hot path). Adding a key builder allocation or struct to every direct request adds overhead for no benefit. The overloads keep the direct path unchanged and add the proxy path without disturbing existing callers.

#### Internal Key Format Unification

Internally, both key formats feed into the same `ConcurrentDictionary<string, Http2Connection>`. The `|via|` separator prevents collisions between direct and proxy keys. No structural changes to the dictionary are needed.

#### `HasConnection` Extension

```csharp
internal bool HasConnection(string originHost, int originPort, string proxyHost, int proxyPort)
{
    return GetIfExists(originHost, originPort, proxyHost, proxyPort) != null;
}
```

### `RawSocketTransport.DispatchViaProxyAsync` Changes

The secure proxy path is restructured into three stages while preserving the existing 22b.2
prepared-lease HTTP/1.1 behavior:

#### Stage 1: Check for Existing Tunneled HTTP/2 Connection (Fast Path)

Before establishing a CONNECT tunnel, check if an HTTP/2 connection already exists for this origin through this proxy:

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
    // Validate proxy
    if (proxy?.Address == null)
        throw new UHttpException(...);

    var proxyHost = proxy.Address.Host;
    var proxyPort = proxy.Address.IsDefaultPort ? 80 : proxy.Address.Port;
    var tunneledRequest = secure
        ? PrepareHttpsProxyTunnelRequest(request)
        : null;

    // Stage 1: HTTP/2 fast path for HTTPS through proxy
    if (secure)
    {
        var h2Conn = _h2Manager.GetIfExists(host, port, proxyHost, proxyPort);
        if (h2Conn != null)
        {
            try
            {
                context.RecordEvent("TransportProxyH2Reuse");
                await h2Conn.DispatchAsync(tunneledRequest, handler, context, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _h2Manager.Remove(host, port, proxyHost, proxyPort);
                if (!CanRetryH2RequestAfterTransportFailure(tunneledRequest))
                    throw;
                context.RecordEvent("TransportProxyH2StaleRetry");
                // Fall through to establish new tunnel
            }
        }
    }
}
```

#### Stage 2: `EstablishConnectTunnelAsync` Returns ALPN Result

The method signature changes to return ALPN information:

```csharp
/// <summary>
/// Result of establishing a CONNECT tunnel, including TLS negotiation details.
/// </summary>
private readonly struct TunnelResult
{
    public readonly Stream SecureStream;
    public readonly string NegotiatedAlpn;
    public readonly string TlsVersion;
    public readonly string TlsProviderName;

    public TunnelResult(Stream secureStream, string negotiatedAlpn, string tlsVersion, string tlsProviderName)
    {
        SecureStream = secureStream;
        NegotiatedAlpn = negotiatedAlpn;
        TlsVersion = tlsVersion;
        TlsProviderName = tlsProviderName;
    }
}

private async Task<TunnelResult> EstablishConnectTunnelAsync(
    Stream proxyStream,
    string targetHost,
    int targetPort,
    ProxySettings proxy,
    CancellationToken ct)
{
    var authority = BuildAuthority(targetHost, targetPort);
    var attemptedAuth = false;

    while (true)
    {
        var connectRequest = BuildConnectRequest(authority, proxy, attemptedAuth);
        var buffer = Encoding.ASCII.GetBytes(connectRequest);
        await proxyStream.WriteAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
        await proxyStream.FlushAsync(ct).ConfigureAwait(false);

        var response = await ReadProxyConnectResponseAsync(proxyStream, ct).ConfigureAwait(false);
        await DrainProxyConnectBodyAsync(proxyStream, response.Headers, ct).ConfigureAwait(false);

        if (response.StatusCode == 200)
        {
            var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);
            // Phase 22c: advertise both h2 and http/1.1 ALPN
            var tlsResult = await tlsProvider.WrapAsync(
                proxyStream,
                targetHost,
                new[] { "h2", "http/1.1" },  // Changed from { "http/1.1" } only
                ct).ConfigureAwait(false);

            if (_tlsBackend == TlsBackend.Auto && tlsResult.ProviderName == "SslStream")
                TlsProviderSelector.MarkSslStreamViable();

            return new TunnelResult(
                tlsResult.SecureStream,
                tlsResult.NegotiatedAlpn,
                tlsResult.TlsVersion,
                tlsResult.ProviderName);
        }

        if (response.StatusCode == 407 && !attemptedAuth &&
            proxy?.Credentials != null && proxy.AllowPlaintextProxyAuth)
        {
            attemptedAuth = true;
            continue;
        }

        // ... error handling unchanged ...
    }
}
```

#### Stage 3: Protocol Routing After Tunnel TLS

Back in the secure proxy path, after a prepared tunnel lease has been acquired / rebound:

```csharp
    // ... (after AcquirePreparedProxyLeaseAsync returns a rebound lease) ...
    var tunneledRequest = PrepareHttpsProxyTunnelRequest(request);

    // Stage 3: Protocol routing based on the prepared lease metadata
    if (lease.Connection.NegotiatedAlpnProtocol == "h2")
    {
        context.RecordEvent("TransportProxyH2Init");
        lease.TransferOwnership(); // Transfer proxy connection to Http2ConnectionManager

        var h2Conn = await _h2Manager.GetOrCreateAsync(
            host, port, proxyHost, proxyPort,
            lease.Connection.Stream, ct).ConfigureAwait(false);

        await h2Conn.DispatchAsync(tunneledRequest, handler, context, ct)
            .ConfigureAwait(false);
        return;
    }

    // HTTP/1.1 fallback — continue through the existing lease-based proxy helper path
    context.RecordEvent("TransportProxyH1Dispatch");
    await DispatchPreparedProxyHttp11Async(lease, tunneledRequest, request, handler, context, ct)
        .ConfigureAwait(false);
```

### Connection Lifecycle

#### Creation Flow

```
Request arrives for https://api.example.com through proxy.corp.com:8080

1. Check _h2Manager.GetIfExists("api.example.com", 443, "proxy.corp.com", 8080)
   → null (first request)

2. _pool.GetConnectionAsync("proxy.corp.com", 8080, secure: false, ct)
   → ConnectionLease for TCP to proxy (semaphore permit acquired for proxy.corp.com:8080)

3. CONNECT api.example.com:443 HTTP/1.1  →  200 Connection Established

4. TLS handshake through tunnel: ALPN { "h2", "http/1.1" } → "h2"

5. lease.TransferOwnership()
   → semaphore permit for proxy.corp.com:8080 released
   → proxy TCP connection ownership transferred

6. _h2Manager.GetOrCreateAsync("api.example.com", 443, "proxy.corp.com", 8080, tlsStream)
   → key = "api.example.com:443|via|proxy.corp.com:8080"
   → Http2Connection created, InitializeAsync (preface + SETTINGS)
   → stored in _connections["api.example.com:443|via|proxy.corp.com:8080"]

7. h2Conn.DispatchAsync(request, handler, context, ct)
   → HTTP/2 HEADERS + DATA frames through tunnel
```

#### Reuse Flow (Subsequent Requests)

```
Second request arrives for https://api.example.com through proxy.corp.com:8080

1. Check _h2Manager.GetIfExists("api.example.com", 443, "proxy.corp.com", 8080)
   → existing Http2Connection (alive)

2. h2Conn.DispatchAsync(request, handler, context, ct)
   → new HTTP/2 stream on existing connection
   → no CONNECT, no TLS, no TCP — just stream multiplexing

Cost: one dictionary lookup + one DispatchAsync
```

#### Death Flow

```
Tunneled HTTP/2 connection dies (GOAWAY / network error / idle)

1. h2Conn.IsAlive → false
   → ReadLoopAsync detected EOF or protocol error
   → all pending streams are failed

2. Next request:
   _h2Manager.GetIfExists(...) → conn is not alive → returns null
   OR
   _h2Manager.GetIfExists(...) → conn, DispatchAsync throws
     → catch block: _h2Manager.Remove("api.example.com", 443, "proxy.corp.com", 8080)
     → fall through to establish new tunnel

3. _h2Manager.Remove(key) → conn.Dispose()
   → Http2Connection.Dispose() → disposes TLS stream
   → TLS stream disposal → disposes inner proxy stream
   → proxy stream disposal → disposes TCP socket to proxy
   → entire tunnel torn down

4. New tunnel established from scratch (Steps 2-7 of Creation Flow)
```

### Semaphore Permit Accounting

The semaphore model requires careful analysis:

**Current direct connection model:**
- `_pool.GetConnectionAsync(host, port, secure)` acquires a semaphore permit for `host:port:s`
- `lease.TransferOwnership()` releases the permit but keeps the connection
- The HTTP/2 connection holds the TCP connection for its entire lifetime
- The semaphore permit is released when the lease is disposed, meaning the HTTP/2 connection does NOT hold a long-lived permit

**Proxy tunnel model:**
- `_pool.GetConnectionAsync(proxyHost, proxyPort, false)` acquires a semaphore permit for `proxyHost:proxyPort:`
- `lease.TransferOwnership()` releases the permit but keeps the proxy connection
- The same semaphore behavior as direct: the long-lived HTTP/2 connection does not hold a proxy semaphore permit

**Implication:** After `TransferOwnership()`, the proxy semaphore permit is freed. This means other requests can open new connections to the proxy. If `maxConnectionsPerHost = 6` and one HTTP/2 tunnel is established, the remaining 5 permits are available for other tunnels to different origins through the same proxy, or for non-tunneled proxy requests. This is correct behavior.

**Risk:** If the HTTP/2 connection dies and the proxy TCP socket is still in the pool's idle queue (it shouldn't be after `TransferOwnership`, but worth verifying), stale proxy connections could accumulate. `TransferOwnership` removes the connection from pool management entirely, so this should not happen.

### `TlsProviderSelector.MarkSslStreamViable()` Guard

The current code in `TcpConnectionPool.CreateConnectionAsync` guards the viability mark:

```csharp
if (_tlsBackend == TlsBackend.Auto && tlsResult.ProviderName == "SslStream")
    TlsProviderSelector.MarkSslStreamViable();
```

The same guard must be applied in `EstablishConnectTunnelAsync` after the tunnel TLS handshake. A successful SslStream handshake through a tunnel is equally valid evidence that SslStream works on this platform. The guard is already present in the proposed design above.

### BouncyCastle Fallback Through Tunnel

The current `TcpConnectionPool.CreateConnectionAsync` has a BouncyCastle fallback path when SslStream fails with a platform TLS exception. The tunnel TLS handshake in `EstablishConnectTunnelAsync` needs the same fallback:

```csharp
if (response.StatusCode == 200)
{
    var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);
    try
    {
        var tlsResult = await tlsProvider.WrapAsync(
            proxyStream, targetHost, new[] { "h2", "http/1.1" }, ct).ConfigureAwait(false);

        if (_tlsBackend == TlsBackend.Auto && tlsResult.ProviderName == "SslStream")
            TlsProviderSelector.MarkSslStreamViable();

        return new TunnelResult(tlsResult.SecureStream, tlsResult.NegotiatedAlpn,
            tlsResult.TlsVersion, tlsResult.ProviderName);
    }
    catch (Exception ex) when (
        _tlsBackend == TlsBackend.Auto
        && TlsProviderSelector.IsPlatformTlsException(ex)
        && TlsProviderSelector.IsBouncyCastleAvailable())
    {
        TlsProviderSelector.MarkSslStreamBroken();

        // The failed SslStream handshake corrupted the tunnel stream.
        // Unlike direct connections, we cannot reconnect — the CONNECT tunnel
        // is established on this specific stream. The entire tunnel must be
        // re-established on a fresh proxy connection.
        //
        // Throw and let the caller retry with a new proxy lease.
        // The retry will use BouncyCastle because SslStream is now marked broken.
        throw new UHttpException(new UHttpError(
            UHttpErrorType.TlsError,
            "SslStream TLS handshake failed through CONNECT tunnel. " +
            "BouncyCastle fallback requires a new tunnel connection.", ex));
    }
}
```

**Key difference from direct connections:** For direct connections, the BouncyCastle fallback can create a new socket and retry immediately. For CONNECT tunnels, the proxy stream is corrupted after a failed SslStream handshake. A new CONNECT tunnel must be established on a fresh proxy connection. The simplest approach is to throw and let the caller's retry logic (or the user) re-issue the request — by then, `MarkSslStreamBroken()` ensures all subsequent attempts use BouncyCastle.

**Alternative — inline retry:** Instead of throwing, `DispatchViaProxyAsync` could catch the TLS error, acquire a new proxy lease, establish a new CONNECT tunnel, and retry the TLS handshake with BouncyCastle. This is more user-friendly but adds complexity. **Decision: implement inline retry** because users behind corporate proxies cannot easily retry manually, and the BouncyCastle fallback is a one-time cost (all subsequent connections use BouncyCastle after `MarkSslStreamBroken`).

```csharp
// In DispatchViaProxyAsync, after EstablishConnectTunnelAsync fails:
catch (UHttpException ex) when (ex.Error.Type == UHttpErrorType.TlsError
    && TlsProviderSelector.IsBouncyCastleAvailable())
{
    // SslStream broken through tunnel — retry with fresh tunnel + BouncyCastle
    lease.Dispose(); // discard corrupted proxy connection
    context.RecordEvent("TransportProxyTlsFallbackRetry");

    using var freshLease = await _pool.GetConnectionAsync(proxyHost, proxyPort, false, ct)
        .ConfigureAwait(false);
    var freshStream = freshLease.Connection.Stream;

    context.RecordEvent("TransportProxyConnect");
    var freshTunnelResult = await EstablishConnectTunnelAsync(
        freshStream, host, port, proxy, ct).ConfigureAwait(false);

    // Now SslStream is marked broken, so BouncyCastle was used.
    // Continue with protocol routing...
    if (freshTunnelResult.NegotiatedAlpn == "h2") { ... }
    else { ... }
}
```

### HTTP/1.1 Stale-Retry Through Proxy

The current proxy path (`DispatchViaProxyAsync`) does not have stale-connection retry for HTTP/1.1 through tunnels. This is an existing gap unrelated to 22c, but the refactoring creates a natural place to add it. **Decision: out of scope for 22c** — HTTP/1.1 stale retry through proxy is a separate concern. 22c focuses on HTTP/2 only. The existing HTTP/1.1 proxy path is unchanged.

---

## Connection Pool Key Analysis

### Pool Interaction Matrix

| Scenario | Pool Key | H2 Manager Key | Who Owns TCP Socket? |
|----------|----------|-----------------|----------------------|
| Direct HTTP/1.1 to `api:443` | `api:443:s` | (none) | ConnectionLease → ReturnToPool |
| Direct HTTP/2 to `api:443` | `api:443:s` | `api:443` | Http2Connection (via TransferOwnership) |
| HTTP forward proxy to `api:80` | `proxy:8080:` | (none) | ConnectionLease → ReturnToPool |
| HTTPS tunnel H/1.1 to `api:443` via `proxy:8080` | `proxy:8080:` | (none) | ConnectionLease → ReturnToPool |
| HTTPS tunnel H/2 to `api:443` via `proxy:8080` | `proxy:8080:` | `api:443\|via\|proxy:8080` | Http2Connection (via TransferOwnership) |

### Connection Count Implications

With `maxConnectionsPerHost = 6`:

**Direct HTTP/2:** One connection multiplexes all requests. 5 permits remain available (returned by `TransferOwnership`). If the HTTP/2 connection handles all traffic, only 1 TCP connection is used.

**Tunneled HTTP/2 to one origin:** One proxy TCP connection is used for the tunnel. The permit is returned by `TransferOwnership`. 5 permits remain for other tunnels to different origins.

**Tunneled HTTP/2 to N origins through one proxy:** N proxy TCP connections (one tunnel per origin). Each creates an HTTP/2 connection with multiplexing. Maximum N = 6 (limited by proxy semaphore before `TransferOwnership`; after, permits are returned). In practice, N can exceed 6 because permits are returned after `TransferOwnership`. The semaphore limits concurrent *establishing* of tunnels, not the number of long-lived HTTP/2 connections.

**Concern:** If many origins are accessed through one proxy, the number of long-lived proxy TCP connections grows unbounded (one per origin). This is correct behavior (each tunnel is a separate TCP connection), but it could exhaust proxy resources. **Mitigation:** The `Http2ConnectionManager` idle timeout (via GOAWAY or lack of active streams) naturally closes idle tunnels. No new limiting mechanism is needed for 22c.

---

## Timeline / Diagnostic Events

New `RecordEvent` calls for proxy HTTP/2 paths:

| Event | Meaning |
|-------|---------|
| `TransportProxyH2Reuse` | Existing tunneled HTTP/2 connection found and reused |
| `TransportProxyH2StaleRetry` | Stale tunneled HTTP/2 connection detected, retrying with new tunnel |
| `TransportProxyH2Init` | New HTTP/2 connection established through CONNECT tunnel |
| `TransportProxyTlsFallbackRetry` | SslStream failed through tunnel, retrying with BouncyCastle |
| `TransportProxyH1Dispatch` | ALPN negotiated HTTP/1.1 through tunnel (fallback) |

These parallel the existing direct-connection events (`TransportH2Reuse`, `TransportH2Init`, `TransportH2StaleRetry`).

---

## Files Impacted

| File | Change |
|------|--------|
| `Runtime/Transport/Http2/Http2ConnectionManager.cs` | Add proxy-tunneled overloads for `GetIfExists`, `GetOrCreateAsync`, `Remove`, `HasConnection`; add `BuildTunnelKey` helper |
| `Runtime/Transport/RawSocketTransport.cs` | Restructure `DispatchViaProxyAsync` with HTTP/2 fast path, protocol routing after tunnel TLS, stale-retry logic; add `TunnelResult` struct; add BouncyCastle fallback inline retry |
| `Runtime/Transport/RawSocketTransport.cs` | `EstablishConnectTunnelAsync` signature change: return `TunnelResult` instead of `Stream`; change ALPN from `{ "http/1.1" }` to `{ "h2", "http/1.1" }` |
| `Runtime/Transport/RawSocketTransport.cs` | `HasHttp2Connection` internal method: add proxy-aware overload |
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | No changes — pool semantics are unchanged; proxy connections are regular `(proxyHost, proxyPort, false)` entries |

### Files NOT Changed

| File | Why |
|------|-----|
| `Runtime/Transport/Http2/Http2Connection.cs` | HTTP/2 connection is protocol-agnostic — it operates on a `Stream` regardless of whether it wraps a direct socket or a proxy tunnel. No changes needed. |
| `Runtime/Transport/Http2/Http2Stream.cs` | Stream multiplexing is transport-agnostic. |
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | Pool manages TCP connections to hosts (proxy or origin). The pool does not know or care whether a connection will be used for tunneling. |
| `Runtime/Core/` | No core type changes. |

---

## Sub-Phase Plan

### 22c.1: `Http2ConnectionManager` Key Extension

**Effort:** 1–2 days

Deliverables:

1. Add `BuildTunnelKey(originHost, originPort, proxyHost, proxyPort)` private static helper
2. Add `GetIfExists(string originHost, int originPort, string proxyHost, int proxyPort)` overload
3. Add `GetOrCreateAsync(string originHost, int originPort, string proxyHost, int proxyPort, Stream tlsStream, CancellationToken ct)` overload — internally delegates to existing `GetOrCreateSlowAsync` with the tunnel key
4. Add `Remove(string originHost, int originPort, string proxyHost, int proxyPort)` overload
5. Add `HasConnection(string originHost, int originPort, string proxyHost, int proxyPort)` overload
6. Verify that direct-connection methods (`GetIfExists(host, port)`, etc.) are completely unchanged — no regression risk

Completion criteria:

- [ ] All proxy-tunneled overloads delegate to the same `ConcurrentDictionary<string, Http2Connection>` with the extended key format
- [ ] Direct-connection overloads are unchanged (no signature or behavior change)
- [ ] Unit test: direct key `"api:443"` and tunnel key `"api:443|via|proxy:8080"` are independent entries
- [ ] Unit test: two different proxies to the same origin produce independent entries
- [ ] Unit test: `GetIfExists` returns null for tunnel key when only direct key exists, and vice versa
- [ ] Unit test: `Remove` with tunnel key does not affect direct key entry
- [ ] Unit test: `GetOrCreateAsync` with tunnel key creates independent connection from direct key
- [ ] Thread-safety: tunnel-key operations use the same `_initLocks` mechanism as direct keys

### 22c.2: `EstablishConnectTunnelAsync` ALPN Extension

**Effort:** 1–2 days

Deliverables:

1. Add `TunnelResult` readonly struct to `RawSocketTransport` (or a separate internal file)
2. Change `EstablishConnectTunnelAsync` return type from `Task<TlsResult>` to `Task<TunnelResult>`
3. Change ALPN list from `new[] { "http/1.1" }` to `new[] { "h2", "http/1.1" }`
4. Populate `TunnelResult` with `NegotiatedAlpn`, `TlsVersion`, `TlsProviderName` from `tlsProvider.WrapAsync` result
5. Add `MarkSslStreamViable()` guard for SslStream through tunnel (same condition as direct path)
6. Add BouncyCastle fallback for tunnel TLS failures: throw `UHttpException(TlsError)` with `MarkSslStreamBroken()`, to be caught by the inline retry in 22c.3

Completion criteria:

- [ ] `EstablishConnectTunnelAsync` returns `TunnelResult` with all TLS metadata
- [ ] ALPN negotiation offers both `h2` and `http/1.1`
- [ ] `MarkSslStreamViable()` called only when `Auto` backend and `SslStream` provider
- [ ] BouncyCastle fallback marks SslStream broken and throws recoverable error
- [ ] All existing callers updated to use `TunnelResult` in the prepared-lease proxy path (`AcquirePreparedProxyLeaseAsync` / `RebindConnectTunnelLease`)
- [ ] Unit test: tunnel TLS with ALPN `h2` returns correct `NegotiatedAlpn`
- [ ] Unit test: tunnel TLS with ALPN `http/1.1` returns correct `NegotiatedAlpn`
- [ ] Unit test: tunnel TLS failure with BouncyCastle available calls `MarkSslStreamBroken()`

### 22c.3: Protocol Routing in `DispatchViaProxyAsync`

**Effort:** 2–3 days

Deliverables:

1. Add HTTP/2 fast path at the top of `DispatchViaProxyAsync`: check `_h2Manager.GetIfExists(host, port, proxyHost, proxyPort)` before establishing a new tunnel, and dispatch the sanitized tunneled request
2. Add stale-connection retry logic for tunneled HTTP/2 using the same `CanRetryH2RequestAfterTransportFailure(...)` gate as the direct path
3. Add protocol routing after prepared tunnel acquisition: branch on `lease.Connection.NegotiatedAlpnProtocol` for `h2` vs `http/1.1`
4. HTTP/2 path: `lease.TransferOwnership()` + `_h2Manager.GetOrCreateAsync(host, port, proxyHost, proxyPort, tlsStream)` + `h2Conn.DispatchAsync(tunneledRequest, ...)`
5. HTTP/1.1 path: existing lease-based proxy/tunnel dispatch semantics remain unchanged
6. Add BouncyCastle inline retry: catch TLS error from prepared tunnel acquisition, reacquire a fresh prepared tunnel, and retry after `MarkSslStreamBroken()` has forced BouncyCastle
7. Add all new `RecordEvent` calls: `TransportProxyH2Reuse`, `TransportProxyH2StaleRetry`, `TransportProxyH2Init`, `TransportProxyTlsFallbackRetry`, `TransportProxyH1Dispatch`

Completion criteria:

- [ ] First request to origin through proxy: CONNECT + TLS + ALPN → HTTP/2 connection created
- [ ] Second request to same origin through same proxy: HTTP/2 fast path, no CONNECT
- [ ] Request to same origin through different proxy: separate CONNECT + HTTP/2 connection
- [ ] Request to different origin through same proxy: separate CONNECT + HTTP/2 connection
- [ ] Direct request to same origin (no proxy): uses direct HTTP/2 connection, not tunneled one
- [ ] Stale tunneled HTTP/2 connection: removed from manager, new tunnel established only when `CanRetryH2RequestAfterTransportFailure(...)` allows replay
- [ ] Non-replayable request on stale tunneled HTTP/2: exception propagated (no retry)
- [ ] ALPN negotiates `http/1.1`: falls back to the existing lease-based HTTP/1.1 proxy dispatch path
- [ ] BouncyCastle fallback: failed SslStream → new tunnel with BouncyCastle → success
- [ ] `TransferOwnership()` correctly releases proxy semaphore permit
- [ ] Timeline events recorded for all paths
- [ ] Unit test: HTTP/2 fast path reuses existing tunneled connection
- [ ] Unit test: stale HTTP/2 tunneled connection retry
- [ ] Unit test: non-replayable stale failure propagation
- [ ] Unit test: ALPN `http/1.1` fallback through tunnel
- [ ] Unit test: BouncyCastle inline retry through tunnel
- [ ] Unit test: concurrent requests to same origin through same proxy retain only one `Http2Connection`; a transient second CONNECT/TLS attempt is acceptable if disposed without leak
- [ ] Unit test: concurrent requests to different origins through same proxy create separate tunnels
- [ ] Integration test: full round-trip through mock CONNECT proxy with HTTP/2 ALPN

### 22c.4: Validation and Edge Cases

**Effort:** 1–2 days

Deliverables:

1. Connection lifecycle validation: tunneled HTTP/2 connection death correctly disposes the entire chain (TLS stream → tunnel stream → proxy socket)
2. GOAWAY handling through tunnel: verify `Http2Connection`'s existing GOAWAY handling works correctly when the underlying stream is a tunnel (no tunnel-specific GOAWAY behavior needed)
3. Concurrent tunnel establishment: verify that two concurrent requests to the same origin through the same proxy retain only one `Http2Connection`; duplicate CONNECT/TLS work inside the accepted race window must be disposed without leak
4. Proxy authentication with HTTP/2: verify that the sanitized tunneled request (`PrepareHttpsProxyTunnelRequest` or equivalent) is used for HTTP/2 requests so `Proxy-Authorization` never reaches the origin
5. `HasHttp2Connection` proxy-aware overload for internal diagnostics

Completion criteria:

- [ ] Connection death tears down entire tunnel chain (TLS → tunnel → socket)
- [ ] GOAWAY through tunnel fails all pending streams and marks connection not alive
- [ ] Concurrent tunnel establishment retains only one `Http2Connection`; duplicate CONNECT/TLS work, if it occurs inside the accepted race window, is disposed without leak
- [ ] Proxy authentication headers not leaked to origin on HTTP/2 path
- [ ] `HasHttp2Connection(host, port, proxyHost, proxyPort)` returns correct result
- [ ] Unit test: connection disposal chain verification
- [ ] Unit test: GOAWAY handling through tunnel
- [ ] Unit test: concurrent tunnel establishment (one retained `Http2Connection`, no leak from any discarded second tunnel)
- [ ] Unit test: proxy auth header isolation

---

## Ordering and Dependencies

```
22c.1 (Manager key extension)   ─── independent
22c.2 (Tunnel ALPN extension)   ─── independent
22c.3 (Protocol routing)        ─── depends on 22c.1 + 22c.2
22c.4 (Validation)              ─── depends on 22c.3
```

22c.1 and 22c.2 can be implemented in parallel. 22c.3 is the main integration point. 22c.4 is validation and edge case hardening.

**Recommended order:** 22c.1 → 22c.2 (parallel if possible) → 22c.3 → 22c.4

---

## Effort Estimate

| Sub-Phase | Name | Effort |
|-----------|------|--------|
| 22c.1 | `Http2ConnectionManager` Key Extension | 1–2 days |
| 22c.2 | `EstablishConnectTunnelAsync` ALPN Extension | 1–2 days |
| 22c.3 | Protocol Routing in `DispatchViaProxyAsync` | 2–3 days |
| 22c.4 | Validation and Edge Cases | 1–2 days |

Total: 5–9 days

---

## Validation

Each sub-phase must pass both specialist agent reviews (unity-infrastructure-architect + unity-network-architect) before marking complete, per project convention.

### Cross-Cutting Test Requirements

- All new tests must run under Unity Test Runner with NUnit
- All new tests go in `Tests/Runtime/Transport/` under appropriate subdirectories
- `MockTransport` and mock proxy infrastructure used for unit tests
- Integration tests that require a real proxy use the `ExternalNetwork` test category
- HTTP/2 connection lifecycle tests reuse existing `Http2ConnectionTests` patterns

### RFC Compliance Matrix

| RFC | Section | Feature | Sub-Phase |
|-----|---------|---------|-----------|
| RFC 9110 | 9.3.6 | CONNECT method semantics | 22c.2, 22c.3 |
| RFC 9113 | 3.2-3.3 | HTTP/2 connection preface and handshake | 22c.3 |
| RFC 7301 | 3 | ALPN protocol negotiation | 22c.2 |
| RFC 9113 | 9.1 | Connection management (GOAWAY) | 22c.4 |

---

## Deferred Until Implementation

1. **Concurrent tunnel establishment race window.** Between `GetIfExists` (returns null) and `GetOrCreateAsync` (acquires init lock), a second concurrent request may also pass `GetIfExists` and attempt to establish a second CONNECT tunnel. The init lock prevents two HTTP/2 connections from being created, but the second tunnel's TLS stream is disposed by `GetOrCreateAsync`'s fast path. The CONNECT handshake and TLS on the second tunnel are wasted work. For direct connections this is acceptable (connection pool handles it). For tunnels, the CONNECT handshake is more expensive. Mitigation options (e.g., a tunnel-establishment lock per origin×proxy) can be evaluated during implementation if profiling shows the race is common.

2. **Tunneled HTTP/2 connection idle timeout.** The `Http2Connection` does not currently have an idle timeout that sends GOAWAY and closes the connection after a period of no active streams. Without this, idle tunneled connections persist indefinitely until the proxy or origin closes them. An idle timeout would be valuable for proxy scenarios where connections are expensive. **Decision: deferred to a general HTTP/2 idle connection management phase.**

3. **Maximum tunneled connections per proxy.** If many origins are accessed through one proxy, many long-lived tunnel connections accumulate. A configurable limit on tunneled HTTP/2 connections per proxy endpoint could be added. **Decision: deferred. Natural idle cleanup is sufficient for v1.**

4. **HTTP/2 CONNECT (RFC 8441).** Using HTTP/2 on the client ↔ proxy leg would allow multiplexing multiple CONNECT tunnels over one proxy connection. This is a fundamentally different architecture. **Decision: deferred to a future phase.**

5. **BouncyCastle ALPN through tunnel.** Verify that BouncyCastle's TLS implementation correctly negotiates ALPN `h2` through a CONNECT tunnel. The BouncyCastle `WrapAsync` should work identically to direct connections, but tunnel-specific TLS behavior (e.g., SNI through proxy) should be validated on physical devices. **Decision: validate during 22c.4 on IL2CPP builds.**
