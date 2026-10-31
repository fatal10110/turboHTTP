# 2026-10 — BouncyCastle Lib in its own assembly; BC tests fail instead of skip (#143, #140)

## What

**#143: the vendored BouncyCastle library compiles as `TurboHTTP.SecureProtocol`.**
- New `Runtime/Transport/BouncyCastle/Lib/TurboHTTP.SecureProtocol.asmdef` with these settings: `references: []`, `noEngineReferences: true`, `autoReferenced: false`, `excludePlatforms: [WebGL]`, rootNamespace `TurboHTTP.SecureProtocol`.
- The asmdef and its `.meta` sit at the `Lib/` root, which the anchored excludes of the #141 re-vendor rsync expect.
- No Lib source was touched.
- `TurboHTTP.Transport.BouncyCastle` (the glue) now references `TurboHTTP.SecureProtocol`.
  - The shadow build confirmed that the glue compiles against Lib as a separate assembly, so no `internal` BC API is in use.
  - The glue assembly name and types are unchanged, so the `Type.GetType` probes in `TlsProviderSelector` and `PlatformConfig` still resolve.
- `Runtime/Transport/BouncyCastle/link.xml` preserves both assemblies with `preserve="all"`.
  - The file stays in the BouncyCastle folder, owned by the glue asmdef, so it merges only when BC ships.
  - It is deliberately not in `Lib/`, because the re-vendor `--delete` sync would remove it.
- `Editor/RepackageBouncyCastle.cs` (+ `.meta`) is deleted. The bc-csharp `strip.sh` sync (#141) is the only sync path.
- `Tools~/ci/check_repo.py`: the glue may reference `TurboHTTP.SecureProtocol`, which itself references nothing and is engine-free. `Development/docs/agents/architecture.md` records the same in its assembly table.
- The phase 3C docs (`step-3c.4`, `overview.md`) carry a superseded note.

**#140: missing BouncyCastle code fails the test instead of skipping it.**
- New `Tests/Runtime/Transport/TestRequirements.cs`:
  - `RequireBouncyCastle()`, `RequireBouncyCastleProvider()` and `RequireBouncyCastleType(name)` fail with the message prefix `[bc-missing] `. They throw `AssertionException` directly, so the failure path itself can be tested with `Assert.Throws`.
  - `IgnoreEnvironment(reason)` is the only remaining way to skip, and its message is prefixed with `[env] `.
- These now fail instead of skipping:
  - `BouncyCastleProviderTests` (12 sites).
  - `TlsIntegrationTests` and `TlsPerformanceBenchmarkTests`.
  - `BouncyCastleLoopbackServer.GetProviderOrIgnore`, renamed `GetProviderOrFail`, and its callers.
  - The 4 CONNECT-tunnel BC requirements in `RawSocketTransportProxyTunnelTests`.
  - The `TlsHostnameValidationTests` reflection helpers.
- The `TlsHostnameValidationTests` helpers resolve `BcTlsCrypto` and `CertificateType` in `TurboHTTP.SecureProtocol`. They never return null now, so the dead `if (x == null) return;` guards were removed.
- Environment-dependent skips keep skipping, now with the `[env] ` prefix:
  - Self-signed certificate generation unavailable.
  - SslStream ALPN unavailable.
  - External TLS endpoints or TCP unreachable (`SslStreamProviderTests`, `TcpConnectionPoolTests`).

## Tests

- `Tests/Runtime/Transport/Tls/BouncyCastleAssemblyTests.cs`:
  - `VendoredLibrary_IsItsOwnAssembly` fails on the old layout, because `TurboHTTP.SecureProtocol` did not exist.
  - `GlueAssembly_ReferencesTheVendoredLibrary` checks the glue's assembly reference.
  - `BouncyCastleProvider_IsPartOfTheTestBuild` checks that the provider is in the test build.
  - `RenamedBouncyCastleType_FailsInsteadOfIgnoring` checks that a renamed Lib type fails with `[bc-missing] ` instead of skipping.
- `Tests/Editor/TurboHttpLinkerProcessorTests.cs`:
  - The BC rule file also preserves `TurboHTTP.SecureProtocol`.
  - That rule appears and disappears with the glue module.

## Decisions

- **Asmdef name:** `TurboHTTP.SecureProtocol`, matching the Lib namespace root and #141.
- **Tests keep reflection plus a fail-fast helper** rather than a direct asmdef reference from `TurboHTTP.Tests.Runtime`.
  - The glue types are `internal`, and a direct reference would need new `InternalsVisibleTo` entries.
  - BC ships in every non-WebGL test build, so failing on absence is correct.
- **Environment skips stay skips** (issue step 2), with the greppable `[env] ` prefix. "Zero Ignored in TLS suites" therefore holds wherever the runtime can generate self-signed certificates.
- **Device scope:** BC tests are scoped to Editor/Standalone. On IL2CPP, `preserve="all"` on both assemblies keeps every reflected member; the device run is tracked in #99.

## Deferred

- The CI gate that treats a non-`[env]` Ignored test as a failure, and compiling `Tests/` in the shadow build: amended into #100 (comment 6539958962). Unity tests currently never run in CI (#188).
- "Editing Core does not recompile BC" holds by construction, because `TurboHTTP.SecureProtocol` has no references. It still needs confirmation in an Editor compile log.
- IL2CPP player build with the split assemblies: #99 (device/CI).

## Verification

- `'./Tools~/ci/check.sh'` passes:
  - Invariants.
  - Format.
  - The shadow build, with `TurboHTTP.SecureProtocol` as its own project and the glue compiling against it.
- `Tests/Runtime` compiled ad hoc with dotnet against the shadow-built modules plus NUnit 3.5. The changed files have no errors. The only errors were in untouched files and came from missing Unity test-framework stubs and `UNITY_INCLUDE_TESTS`.
- **Not run:**
  - Unity EditMode/PlayMode: no Unity license on this host, and CI's Unity job is vacuous (#188).
  - IL2CPP and device builds.
