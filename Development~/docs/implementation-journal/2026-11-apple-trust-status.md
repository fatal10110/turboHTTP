# 2026-11: Apple SecTrust "no chain to an anchor" maps to UntrustedRoot (#239, part)

## What
On iOS 26 (simulator run, #239 comment of 2026-11-03), `SecTrustEvaluateWithError` reports a leaf
whose root is in no store as OSStatus -25318 instead of -67843. `MapEvaluationError` mapped only
-67843 (`errSecNotTrusted`) to `UntrustedRoot`, so this case reported `Invalid` and BouncyCastle sent
`bad_certificate` instead of `unknown_ca`. It always failed closed. Only the status and the alert
were wrong.

-25318 (`errSecCreateChainFailed`) now maps to `UntrustedRoot` in both copies of the mapping.

## Files
- `Runtime/Transport/Tls/AppleTrustVerifier.cs`: `ErrSecCreateChainFailed = -25318` and the new case
  in `MapEvaluationError`. `MacSecTrustVerifier` shares this mapping.
- `Plugins/iOS/TurboHttpSecTrustVerifier.mm`: the mirrored `case errSecCreateChainFailed:` (SDK
  constant, declared unconditionally in `SecBase.h` next to `errSecNotTrusted`).
- `Tests/Runtime/Transport/Tls/AppleTrustStatusMappingTests.cs` (+ `.meta`): the mapping table with
  literal SecBase.h values, fail-closed negatives, and out-of-int-range `long` values that would
  truncate to a mapped code.

## Decisions
- The codes were checked against Apple's `base/SecBase.h` (apple-oss-distributions/Security, main
  db15acb, 2025-10-16):
  - -25318 `errSecCreateChainFailed`: "One or more certificates required to validate this
    certificate cannot be found."
  - -67843 `errSecNotTrusted`
  - -67818 / -67819 expired / not yet valid
  - -67901 `errSecCertificateValidityPeriodTooLong`: "The validity period in the certificate exceeds
    the maximum allowed."
- Only -25318 means "no path to a trusted anchor" (RFC 5280 s6), so only -25318 is added.
- -67901 stays `Invalid`. It is a policy failure on the committed 100-year self-signed test
  certificate (`TestLocalhostCertificate`, `-days 36500`), not a missing anchor. The iOS tests
  `SelfSignedLeaf_WithoutAnchor_IsUntrusted` and
  `BouncyCastleChainValidationTests.Loopback_SelfSignedWithoutAnchor_FailsWithAuthenticationException`
  therefore still fail on Apple until the fixture or the expectation changes. That is follow-up #332.
- These are not added: `errSecInvalidRoot` (-67612, "root ... not valid", a bad root rather than a
  missing one), `errSecInvalidCertAuthority` (-67826), and SecureTransport `errSSLUnknownRootCert`
  / `errSSLNoRootCert` (not reported by SecTrust). There is no evidence for them, and the table
  stays minimal.

## Deferred
- #332: the Apple validity limit on the self-signed test certificate (-67901).
- The device and simulator confirmation stays with #239 and #99 (needs-device-validation).
- The macOS host check stays with #286.

## Verification
- `'./Tools~/ci/check.sh'`: pass. This covers the `check_repo native` result-code cross-check. The
  result codes are unchanged.
- Unity EditMode, scoped filter
  `AppleTrustStatusMappingTests|DesktopCertificateVerifierTests|PlatformCertificateVerifierTests|VerificationFailClosedTests`:
  see the PR.

**Not run:**
- the `.mm`, which was not compiled (no Mac and no Xcode)
- iOS simulator or device
- macOS
- Unity IL2CPP
- the full Unity suites (host editor, periodic)
