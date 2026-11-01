# Vendored BouncyCastle (`Lib/`)

`Lib/` is a reduced, namespace-rewritten copy of [Bouncy Castle for .NET](https://github.com/bcgit/bc-csharp)
(namespace `TurboHTTP.SecureProtocol.Org.BouncyCastle`, assembly `TurboHTTP.SecureProtocol`). It is
produced by the scripted TLS-client strip in [fatal10110/bc-csharp](https://github.com/fatal10110/bc-csharp)
(`strip/strip.sh`, plan `STRIP_PLAN_V2.md`) and synced here unmodified. **Never edit `Lib/` by hand**:
change the strip and re-sync. Licence: `Third-Party Notices.md` at the package root and `Lib/LICENSE.md`.

## Provenance

| | |
| --- | --- |
| Upstream BASE (bcgit/bc-csharp) | `7c9c8d5a12b6342529936f08899cdb752280d538` |
| ROOTS_SHA (turboHTTP commit the closure roots were copied from) | `3856398f548d1c41158567148647df1317e12740` |
| Strip commit synced (fatal10110/bc-csharp `strip-v2`) | `4df803aac3927927fa2cb9c8c892b0291ba26518` |
| Packaged `.cs` files | 681 (the previous hand-vendored copy had 1268) |
| Synced by | turboHTTP#141 |

Sync command (the anchored excludes keep `Lib/TurboHTTP.SecureProtocol.asmdef` from #143):

```
rsync -a --delete --exclude='/*.asmdef' --exclude='/*.asmdef.meta' <bc-csharp>/strip/out/Lib/ Runtime/Transport/BouncyCastle/Lib/
```

The closure roots are the glue files next to this README (`BouncyCastleTlsProvider.cs`,
`BouncyCastleTlsStream.cs`, `TurboTlsClient.cs`, `TurboTlsAuthentication.cs`). When one of them
starts using a new BouncyCastle API, the strip must see the change first (bc-csharp `roots/`,
`roots-extra.cs`), then re-sync.

## Strip decisions (owner-confirmed on #141)

| | Decision |
| --- | --- |
| A | Chain validation via the native verifier or `X509Chain` (#33). BC `X509Certificate.Verify`, `IsSignatureValid` and `VerifyAltSignature` throw `NotSupportedException`. |
| B | No CBC: TLS 1.2 is ECDHE + AEAD only. |
| C | No ML-KEM hybrid groups. |
| D | No OCSP `status_request`, so must-staple is not enforced. |
| E | No Ed25519 or Ed448 certificates; secp521r1 is offered. |
| F | No resumption. |
| G | `AesLightEngine` (or AES-NI) + `BasicGcmMultiplier`; ChaCha20-Poly1305 offered first. |

Other bounds from upstream at BASE: ASN.1 nesting depth 64 (`Properties.Asn1MaxDepth`), RSA peer
moduli 2048-8192 bits with e at most 64 bits, 32 KiB handshake-message cap.

## Certificate validation (#33)

`TurboTlsAuthentication` checks, in order: RFC 6125 host identity (`TlsCertificateNameMatcher`),
leaf EKU serverAuth, leaf validity, path validation outside BC (`ServerCertificateVerifier`:
`X509Chain` in the Editor and on desktop, revocation off; iOS SecTrust and Android
`X509TrustManagerExtensions` on devices, network fetch off, any bridge failure rejects, #239), then `TlsUtilities.CheckPeerSigAlgs` on the platform-built path.
BC APIs it uses (all present in the current Lib, so no re-sync is needed now; the next strip must
take the new `TurboTlsAuthentication.cs` as a root): `X509Certificate`
(`GetSubjectAlternativeNameExtension`, `GetExtendedKeyUsage`, `SubjectDN`, `NotBefore`/`NotAfter`),
`GeneralNames.GetNames`, `GeneralName`, `IAsn1String`, `Asn1OctetString`,
`X509Name.GetValueList`/`X509Name.CN`, `KeyPurposeID`, `TlsCrypto.CreateCertificate(byte[])`,
`TlsUtilities.CheckPeerSigAlgs`.

## Edge-cut list

Every `// turbo-strip:` marker in the packaged files, copied from `STRIP_INFO.md` (paths relative to `Lib/`).

- `asn1/x9/ECNamedCurveTable.cs:5` E6 Asn1.CryptoPro using dropped (ECGost3410NamedCurves cut)
- `asn1/x9/ECNamedCurveTable.cs:37` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:70` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:99` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:132` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:161` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:190` E6 ECGost3410NamedCurves lookup cut (deny: **/*gost*)
- `asn1/x9/ECNamedCurveTable.cs:209` E6 ECGost3410NamedCurves names cut (deny: **/*gost*)
- `crypto/ec/CustomNamedCurves.cs:5` E6 SM2 curve cut; Asn1.GM and Math.EC.Custom.GM usings dropped
- `crypto/ec/CustomNamedCurves.cs:356` E6 SecT* and SM2P256V1 holders cut (deny: math/ec/custom/sec/SecT*, **/*sm2*)
- `crypto/ec/CustomNamedCurves.cs:398` E6 sect*/sm2p256v1 curves and B-/K- aliases cut (DefineCurveAlias throws on a missing OID)
- `crypto/generators/ECKeyPairGenerator.cs:7` E10 (Crypto.Utilities using dropped: ECGost3410Utilities only)
- `crypto/generators/ECKeyPairGenerator.cs:38` E10 ECGost3410Utilities branch removed (GOST denied)
- `crypto/modes/GCMBlockCipher.cs:51` E14 always BasicGcmMultiplier (constant-time GHASH), never the Tables4k lookup tables
- `crypto/parameters/RsaKeyParameters.cs:84` E7 default RSA modulus cap 8192 bits (upstream 16384); the property still overrides
- `crypto/parameters/RsaKeyParameters.cs:92` E7 no Miller-Rabin on peer moduli by default (upstream 8ee52127 costs ~1 s at 8192
- `security/DigestUtilities.cs:403` E11 DSTU7564/GOST3411/GOST3411-2012 digests denied; GetDigest reports them not recognised
- `tls/AbstractTlsClient.cs:103` E4 no finite-field DH (E2 denies DH, DHE and DHE_PSK key exchange)
- `tls/AbstractTlsClient.cs:109` E4 no SRP (E2 denies SRP key exchange)
- `tls/AbstractTlsClient.cs:125` E4 no status_request (decision D: no OCSP request)
- `tls/AbstractTlsClient.cs:161` E4 offer secp521r1 (E1 HasNamedGroup allows it)
- `tls/DefaultTlsClient.cs:12` E3 AEAD-only default suites (decision B: no CBC; no DHE or RSA key exchange)
- `tls/DefaultTlsKeyExchangeFactory.cs:12` E2 no static DH key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:19` E2 no anonymous key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:25` E2 no anonymous key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:31` E2 no finite-field DHE key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:37` E2 no finite-field DHE key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:43` E2 no static ECDH key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:49` E2 no anonymous key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:55` E2 no anonymous key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:72` E2 no PSK key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:79` E2 no PSK key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:85` E2 no RSA key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:92` E2 no SRP key exchange
- `tls/DefaultTlsKeyExchangeFactory.cs:98` E2 no SRP key exchange
- `tls/crypto/impl/bc/BcTlsCrypto.cs:150` E1 no finite-field DH (HasDHAgreement is false)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:160` E1 x448 removed (HasNamedGroup is false for it)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:168` E1 no KEM groups (decision C; HasKemAgreement is false)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:248` E1 gostr3411_2012_256 removed
- `tls/crypto/impl/bc/BcTlsCrypto.cs:260` E1 rsa, ecdsa and rsa_pss_* only (dsa, ed25519, ed448, sm2 removed)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:282` E1 no finite-field DH
- `tls/crypto/impl/bc/BcTlsCrypto.cs:295` E1 AEADs AES-GCM and ChaCha20-Poly1305 only (decision B: no CBC; no CCM, ARIA,
- `tls/crypto/impl/bc/BcTlsCrypto.cs:333` E1 no KEM groups (decision C)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:355` E1 x25519, secp256r1, secp384r1, secp521r1 only (no ffdhe, no KEM/hybrids: decision C)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:371` E1 no RSA key exchange
- `tls/crypto/impl/bc/BcTlsCrypto.cs:379` E1 rsa, ecdsa and rsa_pss_* only (dsa, ed25519/ed448 (decision E), brainpool tls13 removed)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:401` E1 hash whitelist {Intrinsic, SHA-256/384/512}, before the ML-DSA/SLH-DSA early return
- `tls/crypto/impl/bc/BcTlsCrypto.cs:436` E1 ML-DSA, SLH-DSA and sm2sig_sm3 unsupported
- `tls/crypto/impl/bc/BcTlsCrypto.cs:471` E1 no SRP (CreateSrp6* throw)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:522` E1 Gost3411_2012_256Digest removed
- `tls/crypto/impl/bc/BcTlsCrypto.cs:546` E1 Gost3411_2012_256Digest removed
- `tls/crypto/impl/bc/BcTlsCrypto.cs:674` E14 AES-NI engine when available, else the 256-byte S-box AesLightEngine
- `tls/crypto/impl/bc/BcTlsCrypto.cs:797` E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:803` E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
- `tls/crypto/impl/bc/BcTlsCrypto.cs:809` E1 no SRP (abstract in AbstractTlsCrypto, so it stays and throws)
- `tls/crypto/impl/bc/BcTlsHmac.cs:5` E12 (Security using dropped: MacUtilities only)
- `tls/crypto/impl/bc/BcTlsHmac.cs:31` E12 MacUtilities.DoFinal inlined (keeps MacUtilities and its Gost28147Mac out of the closure)
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:11` E5 TurboKeyFactory; Asn1.Cmp, Security and Utilities usings dropped
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:24` E5 Ed25519/Ed448 key fields cut (decision E)
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:67` E5 Ed25519/Ed448 legacy verifier cut (decision E)
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:73` E5 DSA verifier cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:129` E5 RFC 8446 4.2.3: an ecdsa_secp* scheme binds the key's curve
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:141` E5 ed25519/ed448 verifiers cut (decision E)
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:190` E5 sm2sig_sm3, ML-DSA and SLH-DSA verifiers cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:237` E5 DSA public key mapping cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:266` E5 GetPubKeyDss cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:281` E5 GetPubKeyEd25519/GetPubKeyEd448/GetPubKeyMLDsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:296` E5 GetPubKeySlhDsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:337` E5 whitelisting key factory
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:351` E5 SupportsMLDsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:371` E5 SupportsSlhDsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:387` E5 dsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:395` E5 ed25519/ed448 cut (decision E)
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:441` E5 ValidateMLDsa cut
- `tls/crypto/impl/bc/BcTlsRawKeyCertificate.cs:467` E5 ValidateSlhDsa cut
- `x509/X509Certificate.cs:17` E8 (Crypto.Operators and Security usings dropped)
- `x509/X509Certificate.cs:218` E8 turbo OID->name map, OID string when unknown
- `x509/X509Certificate.cs:430` E8
- `x509/X509Certificate.cs:511` E8 no BC certificate-signature verification (decision A): all verify paths throw
- `x509/X509Certificate.cs:530` E8
- `x509/X509Certificate.cs:539` E8
- `x509/X509Certificate.cs:547` E8 VerifyAltSignature and the protected CheckSignature* throw too (decision A)
- `x509/X509Certificate.cs:576` E8 subject key via TurboKeyFactory; a rejection is its plain ArgumentException, not wrapped
- `x509/X509Certificate.cs:580` E8 ToString prints a rejected subject key instead of throwing
- `x509/X509Certificate.cs:593` E8 decision A: chains are validated outside BouncyCastle
- `x509/extension/X509ExtensionUtil.cs:16` E9 SubjectPublicKeyInfoFactory is denied; key identifiers only from an SPKI or certificate

## Per-directory counts

| Directory | .cs files |
| --- | ---: |
| `(root)` | 1 |
| `asn1` | 177 |
| `crypto` | 135 |
| `math` | 120 |
| `runtime` | 1 |
| `security` | 14 |
| `tls` | 193 |
| `turbo` | 2 |
| `util` | 33 |
| `x509` | 5 |
| total | 681 |
