# 2026-10 TLS Auto fails closed, ALPN optional, OS-negotiated TLS version (#34, #165, #110)

## What

- `TlsBackend.Auto` fails closed. `TlsProviderSelector.GetAutoProvider` returns SslStream or throws
  `PlatformNotSupportedException`. It never returns BouncyCastle, which still lacks full chain
  validation (#33). The process-wide "SslStream broken" state is now diagnostic only: it is logged
  once with the exception type and shown in `TlsPlatformCapabilities`, and it never changes routing.
- `TcpConnectionPool.CreateConnectionAsync` no longer catches platform TLS exceptions to reconnect
  and retry on BouncyCastle. Every TLS failure disposes the stream once and propagates.
- `SslStreamTlsProvider`: ALPN is a negotiation (RFC 7301 3.1). Without the ALPN APIs, or when the
  ALPN call fails by reflection, any offer that includes `http/1.1` falls back to a plain handshake
  (no ALPN result, so HTTP/1.1). Only an offer without `http/1.1` (for example `h2` alone) throws.
- `SslStreamTlsProvider` handshakes with `SslProtocols.None` (OS-negotiated). The
  `(SslProtocols)0x3000` constant is deleted. `EnsureMinimumTlsVersion` rejects anything below
  TLS 1.2 after the handshake with `AuthenticationException`, inside the try that disposes the
  SslStream.
- `TlsPlatformCapabilities.RecommendedBackend` never recommends BouncyCastle (SslStream or Auto).
- `IsPlatformTlsException` also unwraps a single-inner `AggregateException` (19a.6 m-2). It now
  only labels diagnostics.
- One-sentence TLS fallback rule (#110), quoted identically in the 22c overview principle 6, the 22c
  main plan (old "BouncyCastle Fallback Through Tunnel" section replaced), 22c.2, 22c.3, 22c.4 and
  `Development/docs/agents/platform.md` (plus a selection matrix):
  > Auto mode picks the TLS provider before the handshake and never picks one without full
  > certificate chain validation; a failed handshake (certificate, authentication, network or
  > platform capability) propagates to the caller with no runtime catch-and-retry on another provider.

## Files

- Runtime/Transport/Tls/SslStreamTlsProvider.cs, TlsProviderSelector.cs, TlsPlatformCapabilities.cs
- Runtime/Transport/Tcp/TcpConnectionPool.cs (removed the retry; added an internal test constructor
  that takes a provider resolver)
- Runtime/Core/TlsBackend.cs (XML docs only)
- Documentation~/PlatformNotes.md, Documentation~/Modules/Transport.md, Development/docs/agents/platform.md
- Development/docs/phases/phase-22c-proxy-http2-alpn.md, phase22c/{overview,phase-22c.2-tunnel-alpn-extension,phase-22c.3-protocol-routing,phase-22c.4-validation}.md
- Tests: new Tests/Runtime/Transport/Tls/SslStreamTlsProviderFailClosedTests.cs,
  Tests/Runtime/Transport/Tls/LocalTlsTestServer.cs, Tests/Runtime/Transport/TcpConnectionPoolTlsFallbackTests.cs;
  updated TlsProviderSelectorTests.cs and TlsPlatformCapabilitiesTests.cs

## Decisions

- No new `TlsBackend` member: explicit `TlsBackend.BouncyCastle` is the opt-in, so the public
  surface changes only in doc text and in behaviour (Auto no longer falls back).
- The broken-state flag is kept, but only for diagnostics, instead of being removed. It logs once
  with its reason, and the internal `ResetProbeState` resets it. Without routing, a per-reason cache
  gains nothing. This supersedes the 19a.6 viability-state design and the 2026-04 22c "inline
  retry" plan decision.
- `NotSupportedException` and `FileNotFoundException` stay in `IsPlatformTlsException`: a false
  positive now only mislabels a diagnostic.
- `FormatTlsVersion` labels any value above `Tls12` as "1.3", because `SslProtocols.Tls13` is missing
  from older profiles.

## Deferred

- 22d.2 Step 4.5 (#110 AC 1/2): the phase22d files are not in the repository. #125 is amended with
  the exact sentence, and #110 stays open (PR says "Part of #110").
- Device runs (IL2CPP without ALPN, Mono/IL2CPP acceptance of `SslProtocols.None`, iOS/Android) are
  `needs-device-validation`, tracked in the #99 matrix.

## Verification

- `'./Tools~/ci/check.sh'`: OK.
- Touched tests built and run on .NET 8 (CoreCLR) in a scratch NUnit project compiling Runtime/Core +
  Runtime/Transport (without BouncyCastle) + the touched test files: 51/51 passed. This includes the
  loopback self-signed TLS handshakes.
- Not run: Unity EditMode/PlayMode (CI only on this host), IL2CPP, devices. On Unity with BouncyCastle
  loaded, the pre-fix code would fail `Auto_WhenSslStreamMarkedBroken_StillReturnsSslStream`, the
  pool platform-exception test (second connection, BouncyCastle retry) and
  `WrapAsync_SimulatedNoAlpn_H2AndHttp11_PerformsPlainHandshake` (PlatformNotSupportedException).
