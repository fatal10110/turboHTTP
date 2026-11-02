# 2026-11 Fail-closed verification results and native desktop certificate verifiers — #278, #248

Closes #278 and #248. macOS validation on real hardware is tracked in #286 (needs-device-validation).

## What

Before:
- `CertificateVerificationStatus.Trusted` was 0, so `default(CertificateVerificationResult)` read as
  trusted. The iOS bridge's success code was 0 too (`kTurboTrustTrusted`, `AppleTrustVerifier.ResultTrusted`).
- The BouncyCastle provider's `VerifyPath` returned `result.Path` for any `Trusted`, without checking
  that a path exists.
- In the Editor and on desktop, BouncyCastle verified chains with `X509Chain`. Unity Mono's
  `X509Chain` cannot verify ECDSA and has an empty root store, so every public server was rejected (#248).

Now:
- **#278 fail-closed convention.**
  - `NotVerified = 0` is the first status, and it is a rejection everywhere. `Trusted = 1`, and every
    member has an explicit value.
  - `CertificateVerificationResult` gained `Trusted(path)` / `Rejected(status, detail)` factories and
    `IsTrusted`, which is true for `Trusted` with a non-empty path. `IsTrusted` is the only success test.
  - `PlatformVerifierRunner.CheckResult` rejects `NotVerified` explicitly.
  - In `TurboTlsAuthentication`, `VerifyPath` goes through `ToBuiltPath`. Everything except
    `IsTrusted` is a fatal alert, and `default`, `Trusted` without a path and unknown values are
    `bad_certificate`.
  - Apple bridge codes: `NotSet = 0` (rejected), `Trusted = 0x54525354` ("TRST"), the others unchanged.
    The `.mm` and the C# constants changed together, and `check_repo.py native` now fails when they
    differ, when `Trusted` is 0, or when anything other than `NotSet` is 0.
- **#248 native desktop verifiers** (owner decision 2026-11-02, option 1):
  - Windows Editor and players: `WindowsCertificateVerifier` (crypt32 P/Invoke, unsafe structs in
    Transport).
    - `CertGetCertificateChain` requires serverAuth for the whole path (`USAGE_MATCH_TYPE_AND`) and
      verifies at the caller's time. It runs with `CERT_CHAIN_CACHE_ONLY_URL_RETRIEVAL |
      CERT_CHAIN_DISABLE_AUTH_ROOT_AUTO_UPDATE | CERT_CHAIN_DISABLE_AIA` and no revocation flags.
    - Then `CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_SSL)` runs with `AUTHTYPE_SERVER` and
      the host. Its only relaxed flags are the revocation-unknown ones.
    - Mapping, the same precedence as every verifier: untrusted or partial chain -> `UntrustedRoot`;
      validity period only -> `NotTimeValid`; anything else (signature, usage, name constraints,
      basic constraints, revoked) -> `Invalid`. Policy errors: `CERT_E_UNTRUSTEDROOT` and `CHAINING`
      -> `UntrustedRoot`, `CERT_E_EXPIRED` -> `NotTimeValid`, anything else `Invalid`.
  - macOS Editor and players: `MacSecTrustVerifier`, C# P/Invoke into Security.framework and
    CoreFoundation. It runs the same flow as the iOS `.mm`: `SecPolicyCreateSSL(true, host)`, network
    fetch off, verify date, `SecTrustEvaluateWithError`, and then the built chain. It shares
    `AppleTrustVerifier`'s codes, `MapEvaluationError` and `Rejection`.
  - Linux Editor and players: no verifier. Verification fails with `NoVerifier` and the detail "TlsBackend.BouncyCastle is
    unsupported on Linux ...".
  - Selection is by define in `ServerCertificateVerifier`. The Editor cases test `UNITY_EDITOR_*`
    first, because the Editor also defines the build target's `UNITY_STANDALONE_*`/`UNITY_IOS`.
    `RequiresPlatformVerifier` is gone: every platform verifies through a platform verifier, or gets
    `NoVerifier`.
  - Test anchors:
    - Windows uses a private chain engine whose exclusive root store holds exactly the anchors.
    - macOS uses `SecTrustSetAnchorCertificates` + `SecTrustSetAnchorCertificatesOnly`.
    - So on desktop the loopback fixtures and `ServerCertificateVerifierTests` exercise the
      production verifier.
    - `X509Chain` is reached only when anchors exist and the platform verifier cannot take them
      (devices, Linux). Production never registers an anchor.

## Files
- `Runtime/Transport/Tls/ServerCertificateVerifier.cs`, `PlatformCertificateVerifier.cs`, `AppleTrustVerifier.cs`
- `Runtime/Transport/Tls/WindowsCertificateVerifier.cs` (new), `MacSecTrustVerifier.cs` (new)
- `Plugins/iOS/TurboHttpSecTrustVerifier.mm` (codes only)
- `Runtime/Transport/BouncyCastle/TurboTlsAuthentication.cs`; doc text in `TlsProviderSelector.cs`,
  `BouncyCastleTlsProvider.cs`, `BouncyCastle/README.md`, `Documentation~/PlatformNotes.md`,
  `Documentation~/Modules/Transport.md`
- `Tools~/ci/check_repo.py` (`check_result_codes`)
- Tests: `ServerCertificateVerifierTests.cs` (hosts now match the leaf SANs, skipped on Linux),
  `PlatformCertificateVerifierTests.cs` (new overload, Apple codes 0 and unknown),
  `VerificationFailClosedTests.cs` (new), `DesktopCertificateVerifierTests.cs` (new)

## Decisions
- **macOS is C# P/Invoke, not the `.mm`.** Unity does not compile a loose `.mm` for the macOS Editor
  or Mono players. A prebuilt `.bundle` cannot be built or validated without a Mac, and calling the
  system frameworks ships no binary. The `.mm` stays the iOS bridge. Both share the result codes
  (checked in CI) and the error mapping (`AppleTrustVerifier`).
- **No network on Windows**, which the `PlatformCertificateVerifier` contract requires. A root the
  machine has not cached is untrusted for BouncyCastle even where SChannel would download it. The
  failure is closed, and Auto/SslStream still gets automatic root updates.
- **The host goes to both platform SSL policies**, as on iOS. The managed RFC 6125 matcher still runs
  first. The platform policy can only reject more.
- **Fail-closed success values.** The status enum's `Trusted = 1`. The native success value is
  distinctive (0x54525354), so a zeroed or bool-like return never reads as success.
- `Development/docs/agents/platform.md` and `project-status.md` still describe the X509Chain desktop
  path. Lanes may not edit them (hot files), so they are left for the next claude-md lane.

## Deferred
- #286: macOS host editor and macOS player validation (needs-device-validation).
- #249 (bc-csharp strip work, not this lane): after merge, the bc-csharp roots need `refresh_roots.py` again.

## Verification
- `./Tools~/ci/check.sh`: pass.
- Unity PlayMode on the Windows host (2021.3.45f2, Mono), filtered: see the PR "Local gates" section.
- Not run: macOS (no Mac, #286), Linux Editor, IL2CPP players, iOS/Android devices (#99).
