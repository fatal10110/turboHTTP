# 2026-11: macOS SecTrust desktop verifier validated on a Mac (#286)

## What

Host validation of the macOS desktop verifier (`AppleTrustVerifier`, SecTrust) with BouncyCastle, in the Editor and in standalone Mono and IL2CPP players. No code changed for #286 itself; fixes found along the way are listed under Deferrals.

## Environment

- Apple M2 (arm64), macOS 26.7.1 (25G241), Xcode 26.4.
- Unity 2021.3.45f2 (Hub install), `TURBOHTTP_BOUNCYCASTLE`.
- main 3131404. Players also include PR #449: without it, 2021.3 IL2CPP players crash in the allocation tests (#414).

## Results

**AC1, Editor.** `./Tools~/unity/run-tests.sh --platform PlayMode --filter 'ServerCertificateVerifier|DesktopCertificateVerifier|VerificationFailClosed|BouncyCastleChainValidation|TlsNegativeSuite'`:
- 113 tests: 111 passed, 0 failed, 2 skipped. The skips are `Windows_TestAnchorsAreTheOnlyRoots` and `Linux_ProductionConfiguration_RejectsWithNoVerifier`, both [env].
- `PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore` passed.

**Standalone players, the same five fixtures, per run:**

| Fixture | Mono (universal, native arm64, stripping Disabled, built on Windows) | IL2CPP (arm64, Stripping High, built on the Mac) |
|---|---|---|
| ServerCertificateVerifierTests | 15/15 | 15/15 |
| DesktopCertificateVerifierTests | 37 passed + 1 skip | 37 passed + 1 skip |
| VerificationFailClosedTests | 12 passed + 1 skip | 12 passed + 1 skip |
| TlsNegativeSuiteTests | 33/33 | 33/33 |
| BouncyCastleChainValidationTests | 14/14 | 14/14 |

Full suite, 3011 tests per run:
- Mono: 3004 passed / 1 failed / 6 skipped, then 3005 / 0 / 6. The one failure is #445, a test race.
- IL2CPP: 3000 / 0 / 11, then 2998 / 1 / 12. The one failure is #169, the clamped-pool stall.

The 19 -67901 failures of the earlier c014e72 Mono run are gone after #332 / PR #422.

**AC2, public hosts.** `--integration --category ExternalNetwork` (40 tests):

| Configuration | Result | TlsPublicHostTests (15) |
|---|---|---|
| IL2CPP, High | 38 passed / 0 failed / 2 skipped | 15/15 |
| Mono, Disabled | 38 passed / 0 failed / 2 skipped | 15/15 |
| Mono, High | 24 passed / 16 failed | #450 |

- Valid ECDSA chain (www.google.com): trusted with `TlsBackend.BouncyCastle`, SslStream and Auto.
- Self-signed, untrusted-root, expired and wrong-host chains (badssl.com): rejected as `CertificateError` on every backend.
- The 2 skips are the [env] "SslStream has no ALPN on this runtime" rows (#430).

## Deferrals

- **AC2 RSA row:** no ExternalNetwork test uses a public host with an RSA leaf. google.com and httpbin.org serve ECDSA leaves now. Still open on #286.
- **#450:** on Mono with Stripping High, `ExeConfigurationHost` is stripped, which breaks every SslStream handshake.
- **#451:** the TLS negative rows pass even when the TLS stack itself is broken.
- **#414 / PR #449:** `GC.GetAllocatedBytesForCurrentThread` is unimplemented in IL2CPP 2021.3.

## Not run

- macOS x86_64 (Intel or Rosetta) players.
- A universal IL2CPP build.
- A Mono player built on the Mac at Low or Medium stripping.
