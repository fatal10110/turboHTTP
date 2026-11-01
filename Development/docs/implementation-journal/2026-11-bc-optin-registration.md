# 2026-11: BouncyCastle opt-in (TURBOHTTP_BOUNCYCASTLE) and provider registration (part of #196, #186)

## What
- **Opt-in.** `Runtime/Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef` has
  `defineConstraints: ["TURBOHTTP_BOUNCYCASTLE"]`. Without the define (Player Settings > Scripting
  Define Symbols) the glue is not compiled, and nothing references the vendored
  `TurboHTTP.SecureProtocol`.
- **Registration instead of reflection (#186).** `TlsProviderSelector` no longer has
  `Type.GetType("TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, ...")` or the
  `FieldInfo` read. It holds one `ITlsProvider` slot. The slot is written with `Interlocked.Exchange`
  and read with `Volatile.Read`.
  - New internal `RegisterBouncyCastleProvider` (null rejected, idempotent) and
    `ExchangeBouncyCastleProviderForTesting`.
  - The glue reaches them through Transport's existing `InternalsVisibleTo`.
  - `IsBouncyCastleAvailable()` is now "a provider is registered".
  - Explicit `TlsBackend.BouncyCastle` with nothing registered throws `InvalidOperationException`
    naming `TURBOHTTP_BOUNCYCASTLE` and `BouncyCastleModuleRegistration.EnsureRegistered()`.
  - `Auto` is unchanged and never selects BC.
- New public `BouncyCastleModuleRegistration.EnsureRegistered()` in the glue. It carries `[Preserve]`,
  `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` and, under `UNITY_EDITOR`,
  `InitializeOnLoadMethod`, the same hooks as `TurboHttpBootstrap`. New glue `AssemblyInfo.cs` with
  `[assembly: AlwaysLinkAssembly]`, because nothing in a player references the glue.
- `TlsPlatformCapabilities.Invalidate()`: a registration that changes the slot drops the cached
  summary, so a late `EnsureRegistered()` is not hidden by an earlier `GetSummary()`.
  `RefreshForTesting()` forwards to it. `RawSocketTransport.ProbeCapabilities` is evaluated on every
  `HttpTransportFactory.Capabilities` read, so it needs no invalidation.
- **Narrowed link.xml.** The rule now roots only `BouncyCastleModuleRegistration` and
  `BouncyCastleTlsProvider` (`preserve="all"` per type). The assembly-wide `preserve="all"` is gone
  from both assemblies, and there is no `TurboHTTP.SecureProtocol` rule at all.
  - Evidence that this is safe: `rg 'Type\.GetType|Activator|GetMethod\(|Assembly\.Load|GetField\('`
    over `Lib/` finds nothing.
  - Lib's only name-based lookup is `DigestUtilities` through `util/Enums.cs`
    (`Enum.GetValues`). Enum members are kept with every enum type the linker keeps.
- Tooling:
  - `check_repo.py` drops `REFLECT_ALLOWLIST`, so the `reflect` check has no exceptions.
  - `shadow_build.py` adds `TURBOHTTP_BOUNCYCASTLE` to `COMMON_DEFINES`, so the glue and Lib stay
    compiled and analyzed.
  - `Tools~/unity/run-tests.sh` writes the define into the test project's
    `ProjectSettings.asset` (`scriptingDefineSymbols: Standalone`). The edit is idempotent and keeps
    the other symbols.
- Docs: README.md (module references), Documentation~/Modules/Transport.md (TLS),
  Documentation~/PlatformNotes.md (TLS row), Runtime/Transport/BouncyCastle/README.md.

## Decisions
- **Define, not a sub-package.** This is the issue's first option. The `TurboHTTP.UniTask` precedent
  keys its define off a package through `versionDefines`, but BC has no package to key on, so the
  user sets the define.
- **No Editor toggle**, deferred to #258 (optional per #196).
- **`Lib/TurboHTTP.SecureProtocol.asmdef` is not constrained**, because CLAUDE.md section 1 lists
  `Lib/` as never-write. Without the glue nothing references it, so the linker has no root for it.
  The player-build proof is #258 / #100.
- **Public `EnsureRegistered()`** mirrors `JsonModuleRegistration`, for projects that call it by
  hand. It is in the opt-in glue assembly, not Core, so the Core API baseline is unchanged.

## Not delivered / deferred
- #196 AC1: no BC assemblies in a player built without the define. Needs a CI player build: #258, #100.
- #196 AC2: BC resolves under Stripping High. Device only: #258, #99.
- CI Unity test job (game-ci package mode) has no define: #258, #188.
- architecture.md lines 22-23 describe the BC asmdefs; the docs-agents lane should mention the define.

## How verified
- `'./Tools~/ci/check.sh'` passes. It covers reflect with an empty allowlist, the asmdef rules and the
  shadow build with the BC glue compiled under the new define.
- The run-tests.sh define edit was checked on sample `ProjectSettings` fragments: `{}`, an existing
  Standalone entry, and a second run.
- Unity 2021.3.45f2 host editor, using a per-lane test project that got the define from the new
  run-tests.sh step:
  - EditMode `--filter 'TurboHttpLinkerProcessorTests|ModuleAutoReferenceTests|TlsProviderSelectorTests|TlsProviderRegistrationTests|BouncyCastleAssemblyTests|PlatformConfigNoReflectionTests|TlsPlatformCapabilit'`:
    38/38 passed.
  - PlayMode `--filter 'TlsProviderSelectorTests|TlsProviderRegistrationTests|BouncyCastleAssemblyTests|PlatformConfigNoReflectionTests|TlsPlatformCapabilit'`:
    51/51 passed. This includes `GlueAssembly_RegistersItsProviderAtStartup`, the self-registration
    with no test call.
  - PlayMode `--filter 'BouncyCastle' --category '!ExternalNetwork'`: 68/68 passed. These are the BC
    handshake suites, running through the registered provider.
- Not run: IL2CPP player builds, Stripping High, device runs.
