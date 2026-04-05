# Phase 22b.2 Review — Streaming Through Proxy Connections

**Reviewers:** unity-infrastructure-architect, unity-network-architect

**Review Date:** 2026-04-07

---

## Overall Verdict

**Both reviews PASS with required fixes and recommended actions.**

The core architecture is sound: the semaphore-key vs pool-key split correctly bounds physical proxy connections while isolating idle tunnels by origin; the lease-based streaming dispatch for proxy connections mirrors the direct HTTP/1.1 path; `CopyWithSharedContent()` eliminates the `Body.ToArray()` copy for both forward-proxy and CONNECT tunnel requests; and the stale-retry pattern is correctly adapted to the proxy path.

One security bug (Proxy-Authorization leak), one build bug (conditional compile guard), two RFC/correctness issues, and one test-server race condition require fixes before sign-off.

---

## Round 1 Findings (2026-04-07)

### Infrastructure Architect Review

| # | Finding | Severity | Category | Action |
|---|---------|----------|----------|--------|
| I-1 | `WaitForAcceptCountAsync` inside `#if TURBOHTTP_INTEGRATION_TESTS` but called from unconditional tests — compile error in standard builds | **HIGH** | Build correctness | Fix required |
| I-2 | `UpdateTransportBinding` writes eight fields with plain stores, no Volatile — ARM64 safety relies implicitly on Task await barrier | **MEDIUM** | Thread safety / IL2CPP | Document invariant |
| I-3 | Test pool key strings (`proxy://…`) do not match production format (`tunnel|host:port:|`) | **LOW** | Test clarity | Fix recommended |
| I-4 | No test for stale-tunnel-retry path (re-CONNECT on stale pooled TCP connection) | **LOW** | Test coverage | Add test |
| I-5 | `IsReused` preserved through `RebindConnectTunnelLease` with no comment explaining proxy-TCP vs tunnel distinction | **LOW** | Documentation | Fix recommended |
| I-6 | `HasUnexpectedData` on TLS-wrapped tunnel streams is best-effort (pre-existing); callsite at `AcquirePreparedProxyLeaseAsync` is new and should note limitation | **LOW** | Documentation | Note only |

**Key Assessment:**
- Lease ownership pattern: ✓ Correct (`lease = null` after `DispatchOnLeaseAsync`, `finally { lease?.Dispose(); }` prevents double-dispose)
- Semaphore vs pool key split: ✓ Correct (semaphore on physical endpoint, idle pool on composite tunnel key)
- `EnqueueConnection` uses `connection.PoolKey` (composite): ✓ Correct
- `AcquirePreparedProxyLeaseAsync` catch: ✓ Correct (bare catch, disposes lease, re-throws)
- Stale retry uses `originalRequest` for replayability: ✓ Correct
- Module boundaries: ✓ Clean (all changes in `TurboHTTP.Transport`)
- IL2CPP/AOT: ✓ No reflection, no dynamic dispatch

---

### Network Architect Review

| # | Finding | Severity | Category | Action |
|---|---------|----------|----------|--------|
| N-1 | `Proxy-Authorization` not removed when credentials exist but `AllowPlaintextProxyAuth == false` — header from original request leaks through | **HIGH** | Security bug | Fix required |
| N-2 | `Proxy-Connection: keep-alive` in `BuildConnectRequest` is a non-standard header; RFC 7230 specifies `Connection` | **MEDIUM** | RFC compliance | Fix required |
| N-3 | Tunnel-reuse guard checks `lease.Connection.IsSecure` instead of comparing pool keys — wrong invariant, currently non-exploitable but fragile | **MEDIUM** | Correctness / latent | Fix recommended |
| N-4 | `RelayTunnelAsync` in test server uses `Task.WhenAny` + external stream dispose, racing against origin-to-client response delivery | **MEDIUM** | Test flakiness | Fix required |
| N-5 | No test for early-dispose through tunnel (drain-or-close policy, explicit completion criterion in spec) | **MEDIUM** | Test coverage | Add test |
| N-6 | ALPN restricted to `http/1.1` in tunneled TLS — documented and correct for Phase 22b; H2-through-CONNECT deferred to Phase 22c | **LOW** | Architecture | Note only |
| N-7 | `IsAlive` polls outer TCP socket only — cannot detect origin-side TLS close through tunnel; stale-retry is the correct safety net | **LOW** | Correctness (pre-existing) | Note only |
| N-8 | Physical proxy + IL2CPP validation (SslStream inside CONNECT tunnel, real proxy, iOS device) required before M3 | **HIGH** | Platform validation | Track separately |

**Key Assessment:**
- RFC 7231 CONNECT authority-form: ✓ Correct (`BuildConnectRequest` produces `CONNECT host:port HTTP/1.1`)
- `Proxy-Authorization` suppressed in inner request after tunnel: ✓ Correct (`PrepareHttpsProxyTunnelRequest` removes it)
- Absolute-form URI for forward proxy (`ProxyAbsoluteForm` metadata key): ✓ Correct
- `CopyWithSharedContent()` shares `Content` reference — `originalRequest.Content.Replayability` accurately reflects `dispatchRequest`: ✓ Correct
- `NegotiatedAlpnProtocol` set correctly after tunnel rebind; routing uses HTTP/1.1 path: ✓ Correct
- `ConnectProxyServer` in test correctly relays raw bytes (TLS ClientHello passes through transparently): ✓ Correct
- Platform: ✓ No new platform-specific concerns beyond pre-existing SslStream/BouncyCastle split

---

## Required Fixes (Blocking)

### Fix 1: HIGH (Security) — `Proxy-Authorization` leaks when `AllowPlaintextProxyAuth` is false

**File:** [Runtime/Transport/RawSocketTransport.cs](../../../Runtime/Transport/RawSocketTransport.cs) — `PrepareHttpProxyForwardRequest`

**Issue:** When `proxy.Credentials != null` and `proxy.AllowPlaintextProxyAuth == false`, `proxyAuthValue` is non-empty. The outer `if` is entered but the inner `if (proxy.AllowPlaintextProxyAuth)` is false, so neither `Set` nor `Remove` runs. A `Proxy-Authorization` header set on the original request passes through unchanged.

```csharp
// Current — missing else branch:
if (!string.IsNullOrEmpty(proxyAuthValue))
{
    if (proxy.AllowPlaintextProxyAuth)
        headers.Set("Proxy-Authorization", proxyAuthValue);
    // BUG: no else — original request's header leaks through
}

// Fix:
if (!string.IsNullOrEmpty(proxyAuthValue))
{
    if (proxy.AllowPlaintextProxyAuth)
        headers.Set("Proxy-Authorization", proxyAuthValue);
    else
        headers.Remove("Proxy-Authorization"); // strip any app-supplied header when plaintext auth is disallowed
}
```

---

### Fix 2: HIGH (Build) — `WaitForAcceptCountAsync` inside `#if TURBOHTTP_INTEGRATION_TESTS`

**File:** [Tests/Runtime/Transport/TcpConnectionPoolTests.cs](../../../Tests/Runtime/Transport/TcpConnectionPoolTests.cs) — line 842

`WaitForAcceptCountAsync` is defined inside the `#if TURBOHTTP_INTEGRATION_TESTS` block (lines 712–883) but is called from two new pool-key tests (`GetConnection_WithPoolKeyOverride_ReuseIsScopedToTunnelKey`, `GetConnection_WithPoolKeyOverride_SemaphoreUsesPhysicalEndpoint`) that are not guarded by the same `#if`. Standard builds without the define will fail to compile.

**Fix:** Move `WaitForAcceptCountAsync` before line 712, outside the conditional block.

---

### Fix 3: MEDIUM (RFC) — `Proxy-Connection` is a non-standard header

**File:** [Runtime/Transport/RawSocketTransport.cs](../../../Runtime/Transport/RawSocketTransport.cs) — `BuildConnectRequest` line 904

`Proxy-Connection` is not defined by any IETF RFC. RFC 7230 §6.3 specifies `Connection` for connection lifecycle management. Most real proxies ignore `Proxy-Connection`, but sending it is a spec violation.

```csharp
// Current:
sb.Append("Proxy-Connection: keep-alive\r\n");

// Fix:
sb.Append("Connection: keep-alive\r\n");
```

---

### Fix 4: MEDIUM (Test) — `RelayTunnelAsync` race in test proxy server

**File:** [Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs](../../../Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs) — `RelayTunnelAsync`

`Task.WhenAny` fires when the client-to-origin pump reaches EOF (after sending request headers, no body). Both streams are then disposed externally before the origin-to-client pump has necessarily delivered the full response. On a lightly loaded machine the 5-byte response clears the TCP kernel buffer first, but the race is real under CI load.

```csharp
// Fix: let each pump handle its own teardown
private static Task RelayTunnelAsync(Stream clientStream, Stream originStream) =>
    Task.WhenAll(
        PumpAsync(clientStream, originStream),
        PumpAsync(originStream, clientStream));

private static async Task PumpAsync(Stream source, Stream destination)
{
    var buffer = new byte[8192];
    try
    {
        while (true)
        {
            var read = await source.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read <= 0)
                break;
            await destination.WriteAsync(buffer, 0, read).ConfigureAwait(false);
            await destination.FlushAsync().ConfigureAwait(false);
        }
    }
    catch (IOException) { }
    catch (ObjectDisposedException) { }
    catch (SocketException) { }
}
```

The caller's `using` blocks in `HandleClientAsync` handle cleanup of both streams.

---

## Recommended Fixes (Non-blocking)

### Rec 1: MEDIUM — Tunnel-reuse guard uses `IsSecure` instead of pool key

**File:** [Runtime/Transport/RawSocketTransport.cs](../../../Runtime/Transport/RawSocketTransport.cs) — `AcquirePreparedProxyLeaseAsync` line 725

```csharp
// Current (fragile — relies on IsSecure state of connection, not key match):
if (!establishTunnel || lease.Connection.IsSecure)
    return lease;

// Recommended (explicit — skip re-establishing only when the pooled connection
// is already bound under the expected tunnel key):
if (!establishTunnel ||
    string.Equals(lease.Connection.PoolKey, poolKeyOverride, StringComparison.OrdinalIgnoreCase))
    return lease;
```

Currently non-exploitable due to composite key isolation, but `IsSecure` is semantically incorrect for this check. Should be corrected before any H2-through-CONNECT work is added.

---

### Rec 2: LOW — Test pool key strings don't match production format

**File:** [Tests/Runtime/Transport/TcpConnectionPoolTests.cs](../../../Tests/Runtime/Transport/TcpConnectionPoolTests.cs) — lines 189–190

Test uses `"proxy://127.0.0.1:{port}|localhost:443"`. Production `BuildConnectTunnelPoolKey` produces `"tunnel|127.0.0.1:{port}:|localhost:443:s"`. The tests validate isolation behavior correctly (both use their own consistent strings), but the format misleads future readers.

Fix: either construct test keys using `TcpConnectionPool.BuildConnectionKey` components, or add a comment that these are arbitrary opaque test identifiers.

---

### Rec 3: LOW — Documentation: `IsReused` meaning through rebind

**File:** [Runtime/Transport/RawSocketTransport.cs](../../../Runtime/Transport/RawSocketTransport.cs) — `RebindConnectTunnelLease`

Add a comment that `IsReused` is intentionally preserved through `UpdateTransportBinding`. It reflects whether the *proxy TCP socket* (not the TLS tunnel) was a pool reuse, so the stale-retry guard in `DispatchProxyHttp11WithRetryAsync` fires correctly for reused proxy TCP connections.

Also add a comment in `UpdateTransportBinding` citing the exclusive-ownership invariant: this method is only called while the lease has exclusive ownership of the connection (before it is returned to callers), so the plain field writes are safe on ARM64 despite the absence of `Volatile.Write` — the `Task` completion boundary provides the required acquire/release barrier.

---

### Rec 4: LOW — Additional test: stale-tunnel-retry

Add a test analogous to `RawSocketTransport_StaleConnection_RetriesOnce` for the proxy path: inject a failing pooled tunnel connection (via `UpdateTransportBinding` + `ReturnToPool` with a `FailingStream`), verify the transport retries by acquiring a fresh proxy connection, re-issues CONNECT, and delivers the response successfully.

---

### Rec 5: MEDIUM — Additional test: early-dispose through CONNECT tunnel

Add a test covering the drain-or-close policy when a streaming response through a CONNECT tunnel is disposed before the body is fully consumed. This is an explicit completion criterion in the phase spec (`Early-dispose of streaming response through tunnel follows drain-or-close policy`). The existing `HttpsViaProxy_StreamingResponseDispose_ReusesConnectTunnel` only tests the fully-consumed case.

---

## Spec Compliance Checklist

| Criterion | Status | Notes |
|-----------|--------|-------|
| Forward proxy body no-copy (`CopyWithSharedContent`) | ✓ PASS | `PrepareHttpProxyForwardRequest` |
| CONNECT tunnel body no-copy (`CopyWithSharedContent`) | ✓ PASS | `PrepareHttpsProxyTunnelRequest` |
| Streaming dispatch through forward proxy | ✓ PASS | `DispatchOnLeaseAsync` via `DispatchProxyHttp11WithRetryAsync` |
| Streaming dispatch through CONNECT tunnel | ✓ PASS | `DispatchOnLeaseAsync` via `DispatchProxyHttp11WithRetryAsync` |
| `ConnectionLease` ownership transfer for streaming proxy responses | ✓ PASS | `lease = null` pattern; body source owns lease lifetime |
| Connection reuse after fully-consumed streaming response through tunnel | ✓ PASS | `HttpsViaProxy_StreamingResponseDispose_ReusesConnectTunnel` |
| Early-dispose of streaming response through tunnel (drain-or-close) | ⚠ UNTESTED | No test covering this path (Rec 5) |
| Pool key prevents incorrect tunnel reuse across origins | ✓ PASS | Composite tunnel key; `HttpsViaProxy_DifferentOrigins_OpenSeparateTunnels` |
| Semaphore bounded by physical proxy endpoint | ✓ PASS | Semaphore key = `(proxyHost, proxyPort)` regardless of origin |
| Stale-retry for forward proxy reused connections | ✓ PASS | `RawSocketTransportTests` |
| Stale-retry for CONNECT tunnel reused connections | ✓ PASS (logic) | No test (Rec 4) |
| CONNECT request uses authority-form URI | ✓ PASS | `BuildConnectRequest` |
| `Proxy-Authorization` not forwarded in inner (tunneled) request | ✓ PASS | `PrepareHttpsProxyTunnelRequest` removes it |
| `Proxy-Authorization` stripped when `AllowPlaintextProxyAuth` false | ✗ BUG | Fix 1 required |
| Absolute-form URI for forward proxy | ✓ PASS | `ProxyAbsoluteForm` metadata key wired correctly |
| CONNECT header: `Connection: keep-alive` (not `Proxy-Connection`) | ✗ NON-STANDARD | Fix 3 required |
| ALPN on inner TLS restricted to `http/1.1` | ✓ PASS (intentional) | H2-through-CONNECT deferred to Phase 22c |
| `Expect: 100-continue` through forward proxy (22b.1 complete) | ✓ PASS | Works unchanged (proxy relays Expect header) |
| `Expect: 100-continue` through CONNECT tunnel (22b.1 complete) | ✓ PASS | Tunnel is transparent; inner connection behaves as direct |
| Physical proxy + IL2CPP validation | 🔄 PENDING | Required before M3 (iOS + Android) |

---

## Thread Safety & IL2CPP Assessment

**Pool key split:**
- Semaphore keyed on `(proxyHost, proxyPort)`: ✓ Correct (bounds physical TCP connections)
- Idle pool keyed on composite tunnel key: ✓ Correct (isolates by origin)
- `EnqueueConnection` reads `connection.PoolKey`: ✓ Correct (uses composite key after rebind)

**`UpdateTransportBinding`:**
- Called only while lease has exclusive ownership (before `return new ConnectionLease(...)` or before caller receives the lease): ✓ Correct by code structure
- Plain field writes: ✓ Safe — `Task` completion boundary at the caller `await` provides acquire/release on all .NET runtimes including IL2CPP ARM64
- `LastUsed` uses `Interlocked.Exchange`: ✓ Correct

**ARM64 IL2CPP:**
- No new reflection, no dynamic dispatch: ✓ Correct
- `string.Equals` with `StringComparison.OrdinalIgnoreCase`: ✓ AOT-safe
- `ConcurrentDictionary`, `ConcurrentQueue`: ✓ AOT-safe (pre-existing)
- Overall: ✓ **SAFE**

---

## Key Observations

1. **Lease ownership pattern is correct.** The `lease = null` / `finally { lease?.Dispose(); }` pattern in `DispatchProxyHttp11WithRetryAsync` and `DispatchCoreAsync` is structurally identical. No double-dispose risk found.

2. **Stale retry semantics are correct.** `IsReused` on the proxy TCP connection (not the TLS tunnel) is the correct predicate for the stale retry guard. Preserved through `UpdateTransportBinding` intentionally.

3. **Replayability check targets the right object.** `originalRequest.Content` and `dispatchRequest.Content` are the same object via `CopyWithSharedContent()`. Checking `originalRequest` is correct.

4. **Composite pool key prevents cross-origin tunnel reuse.** `"tunnel|{proxyHost}:{proxyPort}:|{targetHost}:{targetPort}:s"` provides the necessary isolation. `HttpsViaProxy_DifferentOrigins_OpenSeparateTunnels` verifies this at the transport level.

5. **Security gap in `PrepareHttpProxyForwardRequest`.** The missing `else headers.Remove(...)` branch is small but consequential — it breaks the security guarantee that `AllowPlaintextProxyAuth = false` prevents credential exposure.

---

## Deferred Items

1. **Physical proxy + IL2CPP validation** — SslStream ALPN inside a CONNECT tunnel through a real proxy (Squid 5.x, Charles, mitmproxy) on a physical iOS device. Required before M3. Pre-iOS 15 SslStream ALPN behavior with non-socket streams is known to differ from Mono.

2. **H2-through-CONNECT** — Inner TLS ALPN restricted to `http/1.1` in this phase. HTTP/2 over CONNECT tunnels deferred to Phase 22c.

3. **Spec tests 4, 5, 7, 8 from Phase 22b.1** — Carried forward from 22b.1 deferred list; not 22b.2 scope.

---

## Summary

| Category | Status |
|----------|--------|
| **Architectural soundness** | ✓ Excellent |
| **RFC compliance** | ⚠ One non-standard header (`Proxy-Connection`) — Fix 3 |
| **Security** | ✗ Auth header leak — Fix 1 required |
| **Thread safety** | ✓ Correct (ownership invariant holds) |
| **IL2CPP/AOT safety** | ✓ Correct (no reflection) |
| **Memory efficiency** | ✓ Good (body no-copy, lease reuse) |
| **Test coverage** | ⚠ Two gaps (stale-tunnel-retry, early-dispose-through-tunnel) |
| **Test build correctness** | ✗ `WaitForAcceptCountAsync` compile error — Fix 2 required |
| **Test reliability** | ⚠ `RelayTunnelAsync` race — Fix 4 required |
| **Documentation** | ⚠ `IsReused` / `UpdateTransportBinding` invariants uncommented |

**Recommendation:** Apply Fixes 1–4 (security bug, build bug, RFC header, test race), then request a verification pass before final sign-off.
