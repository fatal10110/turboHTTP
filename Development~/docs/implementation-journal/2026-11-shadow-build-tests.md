# 2026-11: compile the Tests/ asmdefs in the shadow build (#297)

## What

`Tools~/ci/shadow_build.py` now compiles `Tests/Runtime` (`TurboHTTP.Tests.Runtime`) and
`Tests/Editor` (`TurboHTTP.Tests.Editor`), with `UNITY_INCLUDE_TESTS` defined, against the NUnit that
Unity 2021.3 ships (`com.unity.ext.nunit` 1.0.6, `net35/unity-custom/nunit.framework.dll`). Before
this change, a test compile error or an NUnit API that Unity lacks surfaced only in the periodic
host-editor run. `check.sh` (editor variant) now fails on them, and `--full` also compiles the tests
for iOS and Android.

## Files

- `Tools~/ci/shadow_build.py`
  - `Tests` added to `SOURCE_DIRS`.
  - `test_framework()` finds and extracts the two package tarballs (from `<dlls>/packages`, or from
    the `UNITY_PATH` editor's `Data/Resources/PackageManager/Editor`) into `.build/unity-packages/`.
    Extraction keeps regular files and directories only, and only inside the destination.
  - `UnityEngine.TestRunner` and `UnityEditor.TestRunner` are generated from the Test Framework
    1.1.33 source like any asmdef.
  - `precompiledReferences` resolve `nunit.framework.dll`.
  - Test and TestRunner projects use `RunAnalyzers=false`, and `Tests/` warnings are outside the
    ratchet.
  - References to assemblies that are not built for the platform are ignored, as in Unity.
  - An asmdef dropped for an unresolved reference fails the build (`DROPPED`).
  - Added `--no-tests`.
  - `generate()` and `run_variants()` are parameterized (out dir, source root, docs) for the tool
    test.
- `Tools~/ci/fetch-unity-dlls.sh`: also copies `com.unity.ext.nunit-1.0.6.tgz` and
  `com.unity.test-framework-1.1.33.tgz` into `<out>/packages/`, and completes an older cache in
  place.
- `Tools~/ci/check.sh`: always calls the fetch script, which returns at once when the cache is
  complete, and runs the new tool test in `--full`.
- `Tools~/ci/test_shadow_build_tests.py` (new): runs the real generator on scratch test asmdefs.
  - A compile error (CS0103), `Assert.Multiple` (CS0117), `Assert.ThrowsAsync` (CS0117) and an
    unresolved reference (`DROPPED`) each fail the build.
  - `Assert.Throws`, `[UnityTest]`, `LogAssert.Expect` and `NoUnexpectedReceived` compile in the
    editor and ios variants.
  - It skips locally without the packages and fails under `CI`.
- `Tools~/{tls-interop,h2-interop,soak,fuzz,autobahn}/run.sh`: pass `--no-tests`, because they only
  need the runtime DLLs.
- `.github/workflows/ci.yml`: the lint job's Unity cache key is now `unity-managed-tests-<ver>`. A
  cache restored under the old key lacks the packages, and the interop jobs still save
  DLL-only caches under `unity-managed-<ver>`.
- `Development~/docs/agents/tooling.md`: rewrote the "Why a shadow build" paragraph and updated the
  Docker row, the no-Docker section and the ratchet note.

## Decisions

- **Tests in every variant, not editor only.** `TurboHTTP.Tests.Runtime` is also built into the
  IL2CPP players (`run-device-tests.sh`, `run-emulator-tests.sh`, `build-ios-test-player.sh`), so
  the iOS/Android variants catch `#if UNITY_EDITOR` mistakes there. Unity ignores the editor-only
  `UnityEditor.TestRunner` reference in player builds, and the generator now does the same.
- **Packages next to the DLLs.** They live in `.tools/unity-managed/<ver>/packages`, which reuses the
  existing cache path. `unity_dlls()` only references `UnityEngine*`/`UnityEditor*.dll`, so the
  subdirectory leaks nothing. The versions are pinned to what 2021.3.45f2 bundles and what
  `Tools~/unity/*.sh` pins.
- **Analyzer policy.** Ratchet off for tests (#297 allows "off or baselined"). `AssertAsync` blocks
  with `GetResult()` by design, so BannedSymbols does not fit tests.
- **Loud drops.** A silently skipped test assembly would hide every error in it. The check applies
  to all asmdefs. Nothing on main trips it: the UniTask asmdefs fail their define constraint first,
  so they are skipped, not dropped.
- **Kept `check_repo.py unitynunit`.** It also flags `async Task` test methods, which compile but do
  not run in Unity.
- The first run surfaced **no compile errors** in the existing tests (editor, ios, android), so no
  test file changed.

## Deferred

- None for #297. `testing.md` still says "PR CI cannot compile Unity tests without a license" in
  the NUnit-gaps bullet. That bullet is not this lane's file (the lane note names `tooling.md`). It
  is still true for running the tests.

## Verification

- `python3 'Tools~/ci/test_shadow_build_tests.py'`: 2 tests OK. On the old `shadow_build.py`, which
  has no `run_variants`/`test_framework` and no Tests compile, it fails.
- Manual probe: a temporary `Tests/Runtime/ZzProbe/Probe.cs` with `Assert.Multiple` made
  `shadow_build.py` report `error CS0117 Tests/Runtime/ZzProbe/Probe.cs:2`. The probe was removed
  afterwards.
- `shadow_build.py --variant all`: 29/26/26 assemblies, 0 errors, no new warnings.
- `'./Tools~/ci/check.sh' --full`: see the PR's "Local gates" section.
- Not run: Unity EditMode/PlayMode (no test or runtime source changed), IL2CPP player builds and
  devices. Unity tests: host editor (periodic).
