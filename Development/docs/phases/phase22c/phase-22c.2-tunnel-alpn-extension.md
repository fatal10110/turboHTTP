# Phase 22c.2: `EstablishConnectTunnelAsync` ALPN Extension

**Depends on:** Phase 22a (complete)
**Assemblies:** `TurboHTTP.Transport`
**Files modified:** 1

---

## Context

`EstablishConnectTunnelAsync` in `RawSocketTransport.cs` currently advertises only
`s_connectTunnelAlpnProtocols = { "http/1.1" }` for TLS negotiation through the CONNECT tunnel.
The method already returns `Task<TlsResult>` (not a bare `Stream`) — `TlsResult` carries
`SecureStream`, `NegotiatedAlpn`, `TlsVersion`, and `ProviderName`. `RebindConnectTunnelLease`
already consumes all four fields and stores `NegotiatedAlpn` on
`lease.Connection.NegotiatedAlpnProtocol` via `UpdateTransportBinding`.

The only changes required are:

1. Advertise `{ "h2", "http/1.1" }` so the server can negotiate HTTP/2.
2. Handle SslStream failures through the tunnel: unlike direct connections, the tunnel stream is
   corrupted and cannot be reused. Mark SslStream broken and throw a typed error for the inline
   retry in 22c.3.

**No new struct or return-type change is needed.** `TlsResult` already carries all the metadata
22c.3 needs for protocol routing.

This sub-phase is independent of 22c.1 — it touches only `RawSocketTransport.cs`.

---

## Step 1: Change `s_connectTunnelAlpnProtocols`

**File:** `Runtime/Transport/RawSocketTransport.cs` — top of class, line 30

**Before:**
```csharp
private static readonly string[] s_connectTunnelAlpnProtocols = { "http/1.1" };
```

**After:**
```csharp
private static readonly string[] s_connectTunnelAlpnProtocols = { "h2", "http/1.1" };
```

This is the only ALPN change needed. The static array is already passed to
`tlsProvider.WrapAsync(proxyStream, targetHost, s_connectTunnelAlpnProtocols, ct)` inside the
`response.StatusCode == 200` block.

---

## Step 2: Add `MarkSslStreamViable()` and Remove the Runtime Fallback

**BouncyCastle policy:** BouncyCastle is used only when the platform TLS implementation (SslStream)
is genuinely unavailable — i.e., it was stripped from the IL2CPP build. It is **not** a runtime
fallback for TLS handshake failures. A TLS failure through a CONNECT tunnel (cert error, ALPN
mismatch, connection reset) must propagate as-is; it must not trigger a BouncyCastle switch.
`TlsProviderSelector.GetProvider(_tlsBackend)` already returns the correct provider before the
handshake begins — if SslStream is stripped, BouncyCastle is returned automatically without any
catch-and-retry.

The only change in this step is to add `MarkSslStreamViable()` after a successful SslStream
handshake through the tunnel, mirroring the direct-connection path in `TcpConnectionPool`.

Replace the bare `tlsProvider.WrapAsync` call inside the `response.StatusCode == 200` block:

**Before:**
```csharp
if (response.StatusCode == 200)
{
    var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);
    return await tlsProvider.WrapAsync(
        proxyStream,
        targetHost,
        s_connectTunnelAlpnProtocols,
        ct).ConfigureAwait(false);
}
```

**After:**
```csharp
if (response.StatusCode == 200)
{
    var tlsProvider = TlsProviderSelector.GetProvider(_tlsBackend);
    var tlsResult = await tlsProvider.WrapAsync(
        proxyStream,
        targetHost,
        s_connectTunnelAlpnProtocols,
        ct).ConfigureAwait(false);

    if (_tlsBackend == TlsBackend.Auto && tlsResult.ProviderName == "SslStream")
        TlsProviderSelector.MarkSslStreamViable();

    return tlsResult;
}
```

**No catch block.** TLS failures propagate to the caller unchanged. `TlsProviderSelector.GetProvider`
selects the right provider before the handshake — if the platform's SslStream is stripped, it
returns BouncyCastle directly, and no retry is needed.

**Note on `MarkSslStreamViable()`:** The current code returns `tlsResult` directly without the
`MarkSslStreamViable()` call. This step adds parity with the direct-connection path in
`TcpConnectionPool` (lines 583–584). Read the current `EstablishConnectTunnelAsync` body before
coding to confirm the exact placement.

---

## No Other Changes Required

`AcquirePreparedProxyLeaseAsync` and `RebindConnectTunnelLease` already use `TlsResult` and
already flow `NegotiatedAlpn` onto the lease's connection object. After Step 1 enables `h2`
negotiation, `lease.Connection.NegotiatedAlpnProtocol` will reflect the negotiated protocol
automatically. 22c.3 reads it from there for protocol routing.

---

## Completion Criteria

- [ ] `s_connectTunnelAlpnProtocols` changed from `{ "http/1.1" }` to `{ "h2", "http/1.1" }`
- [ ] `MarkSslStreamViable()` guard added (same condition as direct-connection path)
- [ ] No BouncyCastle catch block — TLS failures propagate unchanged; provider selection happens before the handshake via `TlsProviderSelector.GetProvider`
- [ ] `EstablishConnectTunnelAsync` return type stays `Task<TlsResult>` — no new struct introduced
- [ ] `AcquirePreparedProxyLeaseAsync` / `RebindConnectTunnelLease` call sites are **unchanged**
- [ ] Codebase compiles with no new warnings

### Unit Tests

Tests go in `Tests/Runtime/Transport/Http1/RawSocketTransportTests.cs` or a new
`Http1/ProxyTunnelTests.cs` if the suite is large.

Use a mock `ITlsProvider` / test double for `WrapAsync`.

- [ ] ALPN list passed to `ITlsProvider.WrapAsync` contains both `"h2"` and `"http/1.1"` (assert via mock capture)
- [ ] `EstablishConnectTunnelAsync` with mock returning `NegotiatedAlpn = "h2"` → `TlsResult.NegotiatedAlpn == "h2"` (flows through `RebindConnectTunnelLease` onto `lease.Connection.NegotiatedAlpnProtocol`)
- [ ] `EstablishConnectTunnelAsync` with mock returning `NegotiatedAlpn = "http/1.1"` → `lease.Connection.NegotiatedAlpnProtocol == "http/1.1"`
- [ ] `MarkSslStreamViable()` called when `_tlsBackend == Auto` and provider is `"SslStream"` (use mock + verify)
- [ ] TLS failure through tunnel propagates as-is (no catch, no BouncyCastle switch in `EstablishConnectTunnelAsync`)
- [ ] When `_tlsBackend == TlsBackend.BouncyCastle` or SslStream is stripped, `GetProvider` returns BouncyCastle automatically before the handshake (existing `TlsProviderSelector` behavior — no new code, just verify)

---

## Notes for Implementation

- Read `Runtime/Transport/RawSocketTransport.cs` (`EstablishConnectTunnelAsync` and its callers
  `AcquirePreparedProxyLeaseAsync` / `RebindConnectTunnelLease`) before writing code.
- Read `Runtime/Transport/Tls/TlsResult.cs` to confirm exact property names.
- The BouncyCastle catch condition must exactly match `TlsProviderSelector.IsPlatformTlsException` —
  read that method before coding.
- This sub-phase does not add any new public API — all changes are `private`.
