# 2026-10 Re-vendor BouncyCastle from the scripted strip v2 (#141, BC part of #5)

## What
- `Runtime/Transport/BouncyCastle/Lib/` replaced by fatal10110/bc-csharp `strip/out/Lib` at strip
  commit `4df803aac3927927fa2cb9c8c892b0291ba26518` (BASE upstream `7c9c8d5a`, ROOTS_SHA `3856398f`),
  synced with exactly
  `rsync -a --delete --exclude='/*.asmdef' --exclude='/*.asmdef.meta' <bc-csharp>/strip/out/Lib/ Runtime/Transport/BouncyCastle/Lib/`.
  1268 `.cs` files become 681; `Lib/TurboHTTP.SecureProtocol.asmdef` (+ `.meta`, #143) survived; new
  `Lib/turbo/` (TurboKeyFactory, TurboSignatureNames), `Lib/_TurboNamespaceStubs.cs`, `Lib/LICENSE.md`.
  All `.meta` files come from the strip (deterministic GUIDs); nothing references BC GUIDs.
- The new base brings the upstream ASN.1 nesting bound (depth 64, `Asn1InputStream.FindDepth`) that
  the old copy lacked: a hostile certificate no longer recurses without limit on a pool thread.
- `Third-Party Notices.md` (package root, + `.meta`): new file holding the BouncyCastle section from
  `strip/out/THIRD-PARTY-NOTICES.txt` (BC MIT text and the two Turbo-owned `Lib/turbo/` files) with
  the upstream commit. The decoder sections and the release-checklist step stay with #5.
- `Runtime/Transport/BouncyCastle/README.md` (+ `.meta`): provenance (BASE, ROOTS_SHA, strip commit),
  sync command, decisions A-G and the full edge-cut list (E1-E14, 86 markers) from `STRIP_INFO.md`.
- `Tests/Runtime/Transport/Tls/BouncyCastleHostileCertificateTests.cs` (+ `.meta`):
  - `DeeplyNestedServerCertificate_FailsWithBadCertificate`: scripted TLS 1.2 ServerHello
    (ECDHE_RSA_AES_128_GCM, renegotiation_info) then a Certificate whose only entry is 10,000 nested
    indefinite-length SEQUENCE headers (20 KB; a definite-length encoding of that depth exceeds BC's
    32 KiB handshake-message cap). Expects `AuthenticationException` "bad_certificate(42) sent to
    the server" with "maximum nested construction level" in the cause chain.
  - `ClientHello_OffersExactlyTheStrippedCipherSuites`: parses the captured ClientHello and pins
    0x1303, 0x1301, 0x1302, 0xCCA9, 0xC02B, 0xC02C, 0xCCA8, 0xC02F, 0xC030, 0x00FF (SCSV).

## Decisions
- Glue files (`BouncyCastleTlsProvider`, `BouncyCastleTlsStream`, `TurboTlsClient`,
  `TurboTlsAuthentication`) not touched. The strip's roots are from 3856398f; main later rewrote the
  provider and added `BouncyCastleTlsStream` (non-blocking `TlsClientProtocol` API). The shadow build
  compiles all of it against the new Lib, so no member is missing; the strip still owes a run with
  updated roots so its closure and smoke see the current glue (bc-csharp follow-up, see PR).
- Notices file named `Third-Party Notices.md` at the package root, as #5 suggests.
- The SCSV 0x00FF is part of the pinned list: the client sends it instead of an empty
  renegotiation_info extension (RFC 5746 section 3.4).

## Verification
- `'./Tools~/ci/check.sh'` and `--full`: pass (shadow build compiles every asmdef with the new Lib).
- Unity is not used in lanes. As a stand-in, the new tests plus the existing BouncyCastle tests
  (`BouncyCastle*Tests`, `TlsHostnameValidationTests`: 30 tests) were compiled into a scratch NUnit
  project on .NET 8 against the shadow-built DLLs: 30/30 pass with the new Lib. Against a shadow build
  of origin/main (old Lib) both new tests fail: the ClientHello offered
  0x1301, 0x1303, 0xC02B, 0xCCA9, 0xC02F, 0xCCA8, 0x009C, 0x00FF, and the nested certificate failed
  only on end of stream after recursing all 10,000 levels (on Mono's smaller thread stacks that
  recursion is the StackOverflowException the issue describes).
- Not run: Unity EditMode/PlayMode (host editor, periodic), both API Compatibility Levels in Unity
  (bc-csharp #31 pre-checked this byte-identical Lib in 2021.3.45f2 at both levels), IL2CPP,
  iOS/Android device handshakes and AES-GCM/ChaCha throughput (#99).
