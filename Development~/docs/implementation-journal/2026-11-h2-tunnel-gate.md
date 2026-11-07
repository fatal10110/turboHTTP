# 2026-11: HTTP/2 through CONNECT tunnels is opt-in; 22c.4 Editor validation (#103, code half)

## What

- New public option `Http2Options.EnableHttp2ThroughConnectTunnels` (default `false`). By default
  the TLS handshake inside a CONNECT tunnel offers only ALPN `http/1.1`, so requests through a proxy
  use HTTP/1.1 until HTTP/2 through tunnels is device-validated. When `true`, the tunnel offers
  `h2` then `http/1.1` like a direct connection (previous behavior).
- `RawSocketTransport` reads the option once in its constructor. `EstablishConnectTunnelAsync`
  offers `ConnectTunnelAlpnProtocols` (the shared `AlpnProtocols.Http11Only` or
  `AlpnProtocols.H2AndHttp11` array, no per-handshake allocation, #175). Both tunnel routing sites
  (the fresh lease and the stale-HTTP/1.1 retry) hand a lease to the HTTP/2 manager only when the
  option is on and the lease negotiated `h2` (`IsHttp2Tunnel`).
- `Http2Options.Clone()` copies the option and `IsDefault()` checks it, so a client that sets only
  this option still gets a transport built with it (#275 routing in `UHttpClient`).
- Phase 22c.4 checklist: every Editor-testable item is ticked with its test; the device item stays
  open as needs-device-validation.
- `Documentation~/Modules/Transport.md` (Proxies) documents the option, the default and the
  device-validation status (#98 waits on this text).

## Files

- `Runtime/Core/Http2Options.cs`: option, XML docs, `Clone`, `IsDefault`.
- `Runtime/Transport/Tls/AlpnProtocols.cs`: `Http11Only`.
- `Runtime/Transport/RawSocketTransport.cs`: field, `ConnectTunnelAlpnProtocols`, `IsHttp2Tunnel`,
  tunnel ALPN offer.
- `Tests/Runtime/Transport/Http1/RawSocketTransportTunnelAlpnOptionTests.cs` (+ `.meta`): new.
- `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs`: the two tests that inject
  an idle `h2` tunnel lease now build the transport with the option on.
- `Development~/docs/phases/phase22c/phase-22c.4-validation.md`, `Documentation~/Modules/Transport.md`.

## Decisions

- **Enable, default off.** #103 says "option to disable h2 inside CONNECT tunnels, default off",
  which contradicts itself; the lane decision is an option that enables h2, default off, which
  matches the issue title ("ship default-off until validated").
- **Home: `Http2Options`, not `ProxySettings`.** A transport-wide construction-time value: the
  tunnel HTTP/2 connections are shared per origin and proxy endpoint, so a per-`ProxySettings` flag
  would let a request with the flag off reuse an HTTP/2 connection opened by a request with it on.
- **Unoffered `h2` is not trusted.** With the option off, `h2` is never offered (RFC 7301 section
  3.1); a provider that still reports `h2` keeps the lease on HTTP/1.1 rather than starting an
  HTTP/2 connection (RFC 9113 section 3.2: h2 over TLS only through ALPN). A conforming origin
  cannot pick `h2` from an `http/1.1`-only offer, so this changes nothing on the wire for it.
- **22c.4 "Overall" list.** Items that depend on the 22c.1-22c.3 checklists, the specialist reviews
  of each sub-phase and the phase status line are left open with a status note instead of being
  ticked without evidence; #103 stays open (this PR is "Part of #103").

## Tests (new file)

- `Http2Options_TunnelOption_DefaultsOff_ClonesAndLeavesDefault`
- `ConnectTunnelAlpn_OffersSharedListsPerOption`
- `HttpsViaProxy_Default_OffersOnlyHttp11AndUsesHttp11InTunnel` (fails on the old code: the
  tunnel offered `h2, http/1.1`)
- `HttpsViaProxy_Default_ProviderReportingUnofferedH2_StaysOnHttp11`
- `HttpsViaProxy_Enabled_ExplicitBouncyCastleMockNegotiatesH2_AndReusesTunnelConnection`
- `HttpsViaProxy_Enabled_GoawayThroughTunnel_NextRequestOpensNewTunnel` (GOAWAY is sent before the
  stream's response, so ordering on the single read loop makes it deterministic)
- `HttpsViaProxy_Enabled_ConcurrentTunnelEstablishment_RetainsOneConnectionAndDisposesDiscardedTunnel`
  (the mock provider holds both handshakes until both requests opened their own tunnel)
- `HttpsViaProxy_TunnelTlsFailure_PropagatesWithoutNewTunnelOrProviderSwitch`
- `EstablishConnectTunnelAsync_PlatformTlsException_PropagatesWithoutProviderSwitch`

The BouncyCastle provider is a pass-through mock registered with
`TlsProviderSelector.ExchangeBouncyCastleProviderForTesting`; the CONNECT proxy is a loopback
listener that also serves the tunnel (HTTP/2 when the client sends the preface, HTTP/1.1 otherwise).

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Unity 2021.3.45f2 PlayMode (Editor, Windows host) `--filter 'RawSocketTransportTunnelAlpnOptionTests|RawSocketTransportProxyTunnelTests|Http2ConnectionManagerTests|TcpConnectionPoolAlpnArrayTests|TcpConnectionPoolTlsFallbackTests'`:
  37/37 passed (9 new).

## Not run / deferred

- Not run: the full Unity suites (periodic host-editor run), IL2CPP players, devices.
- BouncyCastle ALPN `h2` through CONNECT on iOS/Android IL2CPP and the device journal: #103 (device
  half, blocked on #33 and #99). The phase status line: docs-status work (#98).
