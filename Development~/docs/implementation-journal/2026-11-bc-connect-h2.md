# 2026-11 BouncyCastle over a real CONNECT tunnel negotiating h2 (#255; records for #258)

## What

Three `bc-*` interop rows in the net8 harness `Tools~/h2-interop`. Each runs the production
`RawSocketTransport` with `TlsBackend.BouncyCastle` against Kestrel (in-process), nghttpd and
nginx (docker):

- `bc-get-small`: no proxy. BouncyCastle offers ALPN h2 and completes one HTTP/2 GET.
- `bc-connect-get-small`: through a real CONNECT tunnel (`ConnectProxy.cs`, a new loopback proxy).
  One GET opens the tunnel and negotiates h2 inside it, then 20 concurrent GETs multiplex on the same
  connection. The row checks that:
  - `HasHttp2Connection(origin, port, proxyHost, proxyPort)` is true (origin x proxy key);
  - no direct HTTP/2 connection exists;
  - the proxy accepted exactly one connection and saw exactly one `CONNECT 127.0.0.1:<port>`.
- `bc-connect-get-large`: a 2 MiB body through the tunnel (HTTP/2 flow control over BouncyCastle
  records over the relayed stream), with the same tunnel checks.

No product code changed.

## Files

- `Tools~/h2-interop/ConnectProxy.cs` (new): a minimal CONNECT proxy.
  - It accepts only `CONNECT <loopback-ip>:<port> HTTP/1.1`. Other targets get 403, malformed
    requests 400, and a header block over 8 KiB gets 431.
  - On success it replies `200`, forwards bytes that arrived after the header block, then relays both ways.
  - It counts accepted connections and records the CONNECT targets.
- `Tools~/h2-interop/InteropRows.cs`:
  - Adds the three rows and the `CheckHttp2ViaProxy`/`CheckTunnels` checks.
  - `SendAsync` takes an optional proxy. The request carries `ProxySettings` and
    `RequestMetadataKeys.AllowCleartext` as metadata, which is what `UHttpClient` puts there.
  - Adds the BouncyCastle precondition and the test trust anchor.
- `Tools~/h2-interop/TurboHTTP.H2Interop.csproj`: references the shadow-built
  `TurboHTTP.Transport.BouncyCastle` and `TurboHTTP.SecureProtocol` DLLs, as tls-interop does.
- `Tools~/h2-interop/README.md`, `Tools~/h2-interop/run.sh` (header comment): document the rows.

## Decisions

- **Harness row instead of an Editor test (#255 AC1 wording).** An Editor (Mono) test cannot do it:
  - Mono/UnityTls `SslStream` has no ALPN, so no loopback `SslStream` origin in `Tests/Runtime` can
    select h2.
  - The vendored BouncyCastle strip is client-only. No `TlsServerProtocol` or `DefaultTlsServer`
    exists in `Runtime/Transport/BouncyCastle/Lib/`, and that folder is never-write. So the
    BouncyCastle TLS server proposed in #230 is not available.
  - The `h2-interop` CI job already runs `run.sh` on every PR, so the rows gate PRs with no workflow edit.
  - The Editor itself stays unable to select h2 over a loopback `SslStream` origin. h2 through CONNECT
    on devices stays with #103.
- **h2-interop rather than tls-interop.** h2-interop already hosts a real HTTP/2 server (Kestrel) and
  two independent ones (nghttpd, nginx). `openssl s_server -alpn h2` negotiates ALPN but cannot serve
  HTTP/2 frames, so tls-interop would need Kestrel or a hand-written h2 responder. Adding two DLL
  references to h2-interop was the smaller change.
- **Proof of the path.**
  - The transport is constructed with `TlsBackend.BouncyCastle`. `TlsProviderSelector.GetProvider`
    throws on a missing provider and never falls back.
  - The HTTP/2 connection exists only under the tunnel key (`origin|via|proxy`, RFC 9113 3.2 h2 via ALPN).
  - The proxy saw one tunnel to the origin authority (RFC 9110 9.3.6).
  - Kestrel is HTTP/2-only, so an HTTP/1.1 fallback cannot pass there.
- **Missing provider fails the run (#255 AC2, #140).** `InteropRows.RunAsync` calls both
  `EnsureRegistered()` methods. If `TlsProviderSelector.IsBouncyCastleAvailable()` is still false, it
  prints `FAIL precondition` and exits 1 for every server. It never skips.
- **Trust.** BouncyCastle has no platform verifier on Linux (#248), so the harness registers the
  per-run root with `ServerCertificateVerifier.AddTestTrustAnchor`, as `Tools~/tls-interop` does.
  SslStream ignores that hook and still trusts the root only through `SSL_CERT_FILE`.
- **#258 AC4 (optional Editor toggle): the documented define is enough. No toggle ships.**
  - Player Settings > Scripting Define Symbols already is the per-build-target toggle, and it is
    documented in `Documentation~/Modules/Transport.md`, `Documentation~/PlatformNotes.md` and `README.md`.
  - A TurboHTTP menu item would duplicate it. A toggle that set only the active target's group would
    leave other targets mismatched.
  - It would be Editor-only code that CI cannot test (no Unity license, #188).
  - The host tooling already sets the define where it is needed: `Tools~/unity/run-tests.sh` (test
    project, Standalone group) and `Tools~/unity/player-smoke/Editor/TurboHttpPlayerBuild.cs` (per build).
- **#258 remaining items.**
  - AC1 is delivered as host tooling by #273 (`Tools~/unity/build-player.sh --target android-il2cpp [--bouncycastle]`).
    The CI call of that script stays with #100.
  - AC2 (the provider resolves under Stripping High on iOS/Android) is `needs-device-validation`
    and stays with #99/#103.
  - AC3 (the CI Unity test job gets the define) is withdrawn by the 2026-11-02 owner decision on
    #188/#100: Unity tests run only through host tooling, and `run-tests.sh` writes the define into
    the host test project.
  - Outside Unity, CI now compiles and runs the BouncyCastle glue in two jobs, `tls-interop` and `h2-interop`.

## Deferred

- #103: h2 through CONNECT with BouncyCastle on iOS/Android devices.
- #99: device IL2CPP matrix (the #258 AC2 device part).
- #100: the CI call of `build-player.sh` (the #258 AC1 CI part).

## How verified

- `'./Tools~/h2-interop/run.sh' --only interop --no-shadow-build` (dotnet 8, docker): kestrel 9/9,
  nghttpd 8/8 (it does not serve `/headers`), nginx 9/9. All `bc-*` rows pass.
- Mutation checks, each reverted:
  - With the tunnel ALPN list set to `{ "http/1.1" }` (`RawSocketTransport.cs:30`), `bc-connect-get-small`
    and `bc-connect-get-large` FAIL. Kestrel answers `no_application_protocol(120)`.
  - With `BouncyCastleModuleRegistration.EnsureRegistered()` removed from the harness, the run prints
    `FAIL precondition: the BouncyCastle TLS provider is not available (#140)` and exits 1.
- `'./Tools~/ci/check.sh'`: see the PR "Local gates".
- Not run: Unity EditMode/PlayMode (no Unity test changed), IL2CPP player builds (no asmdef or
  `link.xml` change), devices (#103, #99).
