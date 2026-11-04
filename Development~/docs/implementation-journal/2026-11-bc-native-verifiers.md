# 2026-11 BouncyCastle native certificate verifiers (iOS SecTrust, Android TrustManager) — #239

Part of #239 and #33. Device rows (#239 criteria 4–5) stay open: needs-device-validation (#99).

## What

Before: on `(UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR`, `ServerCertificateVerifier.Verify`
returned `NoVerifier` for every chain, so `TlsBackend.BouncyCastle` could not connect on a device
(fail closed, by design until now).

Now the mobile path goes through a native verifier seam:

- `ServerCertificateVerifier.Verify(chainDer, host, time)` gained the host (platform form,
  `TlsHostName.ToTargetHost`). On mobile with no test anchors it calls the registered
  `PlatformCertificateVerifier` through `PlatformVerifierRunner`; with none registered, `NoVerifier`
  as before. Editor/desktop keep the `X509Chain` path unchanged (#248 owns the desktop decision).
- `PlatformVerifierRunner` (Transport, engine-free) is fail-closed: the bridge runs on a dedicated
  worker thread bounded by a timeout (10 s, under the 30 s handshake deadline that cannot interrupt
  a synchronous call, #142); a timed-out worker is abandoned and counted, and at 4 abandoned workers
  every new verification is rejected without starting a thread. A throw, timeout, unknown status,
  missing/empty/oversized (> 11) path, a non-SEQUENCE element or a path whose first element is not
  the server's leaf byte-for-byte is `Invalid`. Rejections pass through without a path. The bridge
  gets a private copy of the chain.
- iOS: `Plugins/iOS/TurboHttpSecTrustVerifier.mm` (Security.framework + Foundation only, `extern
  "C"`, CF objects with an RAII holder, ARC-agnostic): `SecCertificateCreateWithData` per cert,
  `SecTrustCreateWithCertificates` with `SecPolicyCreateSSL(true, host)`,
  `SecTrustSetNetworkFetchAllowed(false)`, `SecTrustSetVerifyDate`, `SecTrustEvaluateWithError`;
  the built chain from `SecTrustCopyCertificateChain` (`@available` iOS 15 / macOS 12) or
  `SecTrustGetCertificateAtIndex`. Error mapping: expired / not yet valid -> `NotTimeValid`,
  `errSecNotTrusted` -> `UntrustedRoot`, everything else (host mismatch included) -> `Invalid`.
  Result buffer malloc'd by the bridge, freed once by `AppleTrustVerifier` in `finally`
  (`TurboHttp_SecTrustFree`). The C# side (`Runtime/Transport/Tls/AppleTrustVerifier.cs`) is
  `DllImport("__Internal")` under `#if UNITY_IOS && !UNITY_EDITOR`, installed as the field
  initializer of `s_platformVerifier`; the buffer parser and code mapping compile everywhere and are
  tested. `.mm.meta` copies the background-bridge meta (iOS/tvOS, Editor off) and declares
  `FrameworkDependencies: Security;`.
- Android: `Runtime/Transport/BouncyCastle/AndroidTrustManagerVerifier.cs` (BC glue, under
  `#if UNITY_ANDROID && !UNITY_EDITOR`): raw `AndroidJNI` on framework classes only —
  `TrustManagerFactory(default).init(null)` -> first `X509TrustManager` -> `X509TrustManagerExtensions`
  (created once, kept as a global ref under a lock), `CertificateFactory("X.509")` per DER cert,
  `checkServerTrusted(chain, "RSA", host)`, the returned `List` -> DER. One local frame per call;
  attaches the worker thread and detaches it before the thread exits. `CertificateException` with an
  expiry exception in the cause chain -> `NotTimeValid`, other `CertificateException` ->
  `UntrustedRoot`, anything else -> `Invalid`; details never carry exception messages (they can
  name certificates). Registered lazily by `TurboTlsAuthentication.NotifyServerCertificate`
  (`EnsureRegistered`, Android only) through `ServerCertificateVerifier.RegisterPlatformVerifier`
  (set-once CAS).

## Files

- `Runtime/Transport/Tls/ServerCertificateVerifier.cs` (host parameter, seam overload, hook,
  `RequiresPlatformVerifier` replaces `IsUnsupportedPlatform`, `SameBytes` internal, remarks)
- `Runtime/Transport/Tls/PlatformCertificateVerifier.cs` (new: delegate, limits, runner)
- `Runtime/Transport/Tls/AppleTrustVerifier.cs` (new), `Plugins/iOS/TurboHttpSecTrustVerifier.mm` (+ `.meta`, new)
- `Runtime/Transport/BouncyCastle/AndroidTrustManagerVerifier.cs` (new),
  `Runtime/Transport/BouncyCastle/TurboTlsAuthentication.cs` (host to `VerifyPath`, Android registration)
- `Tests/Runtime/Transport/Tls/PlatformCertificateVerifierTests.cs` (new),
  `Tests/Runtime/Transport/Tls/ServerCertificateVerifierTests.cs` (host argument)
- Docs: `Runtime/Transport/BouncyCastle/README.md`, `Documentation~/Modules/Transport.md`,
  `Documentation~/PlatformNotes.md`

## Decisions

- **Seam placement.** Hook and runner in Transport (engine-free). iOS needs no engine (`DllImport`),
  so Transport installs it itself. Android JNI needs UnityEngine, so the bridge lives in the BC glue:
  already engine-referencing (`[Preserve]`), already an `InternalsVisibleTo` friend of Transport, the
  only consumer of the verifier, not in `ENGINE_FREE`, and it references neither TurboHTTP.Unity nor
  anything new. No asmdef reference or `InternalsVisibleTo` was added. Lazy registration from the
  handshake instead of `RuntimeInitializeOnLoadMethod`: it cannot be skipped by stripping or init
  order, and without it the result is still `NoVerifier` (fail closed).
- **Timeout by a dedicated thread + `Thread.Join(timeout)`**, not `Task.Wait` (banned) and not a
  pool work item (a pool-starved handshake would time out spuriously). One short-lived thread per
  mobile handshake; abandoned threads are capped. A dedicated thread also makes JNI attach/detach
  well defined.
- **Host.** `TlsHostName.ToTargetHost(_targetHost)`: A-label, bare IP. iOS SSL policy matches it
  (an extra host check); Android uses it only for per-domain network security config and CT — host
  matching stays with `TlsCertificateNameMatcher` on both (RFC 6125 s6).
- **Android authType "RSA"**, as OkHttp's `AndroidCertificateChainCleaner`: the platform
  `TrustManagerImpl` only requires a non-empty value. Unverified on device (#99).
- **Verification time.** iOS evaluates at the caller's time (`SecTrustSetVerifyDate`, same as the
  `X509Chain` path); Android has no date parameter and evaluates at the current time (BC passes
  `UtcNow` anyway; the leaf validity is also checked against it in `ValidateLeaf`).
- **Test anchors** keep forcing the `X509Chain` path on devices too (loopback fixtures); production
  never registers one.
- Bridge failures map to `Invalid` (`bad_certificate`), a missing bridge to `NoVerifier`
  (`certificate_unknown`); both surface as `UHttpErrorType.CertificateError`.

## Deferred

- #239 criteria 4–5 (Let's Encrypt success, untrusted/self-signed/expired/wrong-host rejection on
  iOS and Android IL2CPP devices): needs-device-validation, #99. Also device-only: SecTrust error
  codes, CT behavior for public certs on iOS, JNI attach/detach on IL2CPP worker threads, authType.
- Desktop (macOS SecTrust reuse of the `.mm`, Windows crypt32): #248, owner decision pending.

## Verification

- `'./Tools~/ci/check.sh' --full`: invariants, format, shadow build editor + ios + android variants
  with analyzers pass (the `ios` and `android` variants compile the `#if` bridge C#). The `--full`
  shellcheck step reports SC2015 in `Tools~/unity/run-tests.sh`, a file this lane does not touch
  (pre-existing).
- Scoped Unity PlayMode run: see the PR "Local gates" section.
- NOT run: an iOS Xcode build (the `.mm` was not compiled: no Xcode on this host), an Android IL2CPP
  player build (#89 breaks Gradle builds), any device run, Let's Encrypt on device.
