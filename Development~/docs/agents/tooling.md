# Tooling: install, configure, verify

Everything a human or an agent needs to run TurboHTTP's quality gates locally, and how CI and
the `main` ruleset enforce them. Applies to every environment (macOS, Linux, Windows/WSL,
cloud agent containers).

## What runs where

| Gate | Local command | CI job | Blocks merge |
| --- | --- | --- | --- |
| Repo invariants: every imported asset has a `.meta`, no orphan/duplicate GUIDs, asmdef dependency graph follows `architecture.md`, JSON/asmdef/package.json valid | `python3 "Tools~/ci/check_repo.py"` | `lint` | yes |
| Whitespace format (`.editorconfig`, `dotnet format whitespace`) | `"Tools~/ci/check.sh"` (`--fix` to apply) | `lint` | yes |
| Shadow build: every runtime/editor/sample asmdef compiled by the .NET SDK for the Editor, iOS and Android define sets, with Roslyn analyzers and banned APIs (`Tools~/ci/BannedSymbols.txt`), warnings ratcheted by `Tools~/ci/analyzer-baseline.txt`; the `Tests/` asmdefs compiled against Unity's own NUnit (compile errors only, #297) | `"Tools~/ci/check.sh"` (editor) / `--full` (all) | `lint` | yes |
| actionlint, shellcheck, Python tool tests, issue-seed validation | `"Tools~/ci/check.sh" --full` | `lint` | yes |
| Unity Test Runner, EditMode + PlayMode (Unity 2021.3.45f2); skip baseline `Tools~/unity/skip-baseline.txt` | `"Tools~/unity/run-tests.sh" --platform EditMode\|PlayMode [--filter X]` (full runs only in the host editor) | none: CI has no Unity license (#188) | no; host-editor gate, `testing.md` "Host-editor gates" |
| Internet-facing tests (`ExternalNetwork`, incl. the `TURBOHTTP_INTEGRATION_TESTS` blocks, #263) | `"Tools~/unity/run-tests.sh" --platform PlayMode --category ExternalNetwork` | none | no; host-editor gate |
| Player builds (Mono Win64, IL2CPP Android with/without BouncyCastle, IL2CPP Win64) and linker-output checks | `"Tools~/unity/build-player.sh" --target ...` | none | no; host-editor gate |
| PlayMode suite as an IL2CPP player on one Android device over adb (#99) | `"Tools~/unity/run-device-tests.sh" [--stripping Low\|High] [--arch arm64\|armv7\|x86_64] [--serial id [--skip-build]]` | none | no; host-editor gate, `testing.md` step 4 |
| PlayMode suite on x86_64 Android emulators, one per API level (#99) | `"Tools~/unity/run-emulator-tests.sh" [--api 23,26,29,33,36] [--jobs 2]` | Android SDK with `emulator` and `system-images;android-<N>;default;x86_64` (`TURBOHTTP_EMULATOR_SDK`), WHPX/KVM; from WSL, `powershell.exe`/`taskkill.exe` stop leftover Windows emulators (`emulator-windows.sh`) | no; host-editor gate, `testing.md` step 4 |
| PlayMode suite as an iOS Simulator SDK IL2CPP player: exports the Xcode project for a Mac to run (#99) | `"Tools~/unity/build-ios-test-player.sh" [--out <dir>] [--filter ...]` | none | no; host-editor gate, `testing.md` step 5 |
| CodeQL (C#, build-mode none; GitHub Actions) | — | `CodeQL` | errors / high+ security alerts |
| Release on a `v*.*.*` tag: tag on `main`, tag = `package.json` version, notes from the `CHANGELOG.md` section, GitHub (pre-)Release | `python3 'Tools~/ci/release_notes.py' vX.Y.Z` (preview) | `Release` (`release.yml`, tags only) | n/a; steps in `../release-checklist.md` |
| Copilot code review, code quality | — | ruleset | advisory / errors |

The Unity scripts above (except `build-ios-test-player.sh`) exit 0 pass, 1 failures, 2 setup, 3 Unity unavailable,
4 skip gate (the test runners) and 5 WSL interop unavailable: from WSL with a Windows editor, `cmd.exe`, `adb.exe`
or `Unity.exe` did not start through about two minutes of retries (`Tools~/unity/wsl-interop.sh`, #355,
`testing.md`). Exit 5 is an infrastructure outage, never a result: rerun.

The required status check is the aggregate job `ci` (`.github/workflows/ci.yml`), which passes
only when `lint`, `tls-interop` and `h2-interop` pass. None of them needs Unity: the repository has
no Unity license and CI never gets one (owner decision 2026-11-02, #188), so there is no
`unity-tests` job that could report a pass it never earned. The Unity gates (full EditMode/PlayMode
runs with the skip baseline, the `ExternalNetwork` run, player builds) are run by hand in a host editor
(`testing.md`, "Host-editor gates") after each waves run and before a milestone closes.

Why a shadow build: Unity compiles the package itself, but CI and agents need compiler errors
and analyzers without a Unity install or license. `Tools~/ci/shadow_build.py` generates one
`.csproj` per asmdef (references, `allowUnsafeCode`, `noEngineReferences`, define constraints,
include/exclude platforms) under `.build/shadow/<variant>/`, references Unity's managed DLLs,
and mirrors Unity 2021.3's compiler setup (.NET Standard 2.1, C# 9, `NETSTANDARD2_1` without the
SDK's `*_OR_GREATER` symbols).

The `Tests/` asmdefs are compiled too (#297), with `UNITY_INCLUDE_TESTS` defined, in every variant:

- **NUnit is Unity's.** It is `nunit.framework.dll` from `com.unity.ext.nunit-1.0.6.tgz`, not a
  NuGet NUnit, so an API Unity's NUnit lacks (`Assert.Multiple`, `Assert.ThrowsAsync`, …) is a
  compile error (CS0117).
- **TestRunner comes from source.** `UnityEngine.TestRunner` and `UnityEditor.TestRunner` are
  generated as projects from the source of `com.unity.test-framework-1.1.33.tgz`, the same versions
  that `Tools~/unity/*.sh` pins.
- **Where the packages come from.** Unity 2021.3 bundles both tarballs in
  `Editor/Data/Resources/PackageManager/Editor`. `fetch-unity-dlls.sh` copies them into
  `.tools/unity-managed/<ver>/packages/`; with `UNITY_PATH`, the build reads them from the editor.
  They are extracted into `.build/unity-packages/`.
- **Platforms.** In the iOS/Android variants, the editor-only `UnityEditor.TestRunner` reference of
  `TurboHTTP.Tests.Runtime` is ignored, as Unity does in a player build, so these variants compile the
  tests as the device and emulator runs build them.
- **No analyzers.** Test projects build with `RunAnalyzers=false` and their warnings are outside the
  ratchet. Compiler errors fail. Tests may still use `AssertAsync`'s `GetResult()`, which is banned
  in shipped code.
- **No silent drops.** An asmdef that passes its platform and define constraints but references an
  assembly the build cannot provide fails the build (`DROPPED`). Before, it was skipped silently.
- **UniTask adapters (#311).** With the tests, the build fetches UniTask at the commit pinned in
  `Tools~/ci/unitask_pin.py` (see "UniTask pin" below) into `.tools/unitask/<sha>/` with git, once,
  and compiles its runtime asmdef (`UniTask`, external: no analyzers, outside the ratchet; its own
  `Linq`/`External/*` asmdefs are not built). `TurboHTTP.UniTask`, `TurboHTTP.UniTask.WebSocket` and
  `TurboHTTP.Tests.UniTask` then get `TURBOHTTP_UNITASK` from their `versionDefines`, scoped to those
  asmdefs as in Unity, and compile in every variant. UniTask's own `versionDefines` on
  `com.unity.modules.*` are not applied, so its module-specific code (physics, UI, …) is not built.
  The first build needs git and access to github.com; offline, `--no-unitask` (on `shadow_build.py`
  or `check.sh`) skips the fetch and prints that the adapters were not compiled.
- **Opting out.** `shadow_build.py --no-tests` builds only the shipped assemblies, without the UniTask
  fetch, so it never needs the network. The interop, soak, fuzz and Autobahn `run.sh` scripts use it.
- **Proof.** `Tools~/ci/test_shadow_build_tests.py` (in `check.sh --full`) proves that a compile
  error, `Assert.Multiple`, `Assert.ThrowsAsync` and an unresolved reference each fail the build.
  `Tools~/ci/test_unitask_pin.py` (also `--full`, no network) covers the pin: the fetch against a
  local git remote (wrong commit, moved tag), the `packages-lock.json` check, the result check and the
  `versionDefines` wiring.

`check_repo.py unitynunit` and `ignores` stay: they also catch tests that compile but that Unity
does not run (`async Task` test methods).

## Required tools

| Tool | Version | Used by |
| --- | --- | --- |
| git | any recent | everything |
| Python | 3.9+ (stdlib only) | `check_repo.py`, `shadow_build.py`, agent tools, issue seed |
| .NET SDK | 8.0.x | `dotnet format`, shadow build |
| Docker | any (daemon running) | `fetch-unity-dlls.sh` (one-time, pulls `unityci/editor:ubuntu-<ver>-base-3.2.2`, ~4 GB, keeps 35 MB: managed DLLs plus the NUnit and Test Framework package tarballs; an older cache without `packages/` is completed in place) |
| GitHub CLI `gh` | 2.40+ | agent workflows, issue seed, PR handling |
| ripgrep `rg` | any | agent tools |
| actionlint | 1.7+ | `check.sh --full` |
| shellcheck | 0.9+ | `check.sh --full` |
| Node.js | 18+ | `turbohttp-waves` simulation (`waves_sim.mjs`) |
| Unity | 2021.3.45f2 (optional locally) | `run-tests.sh`, `build-player.sh`; CI does not run Unity (no license, #188), they run in a host editor (testing.md, "Host-editor gates") |

### macOS (Homebrew)

```bash
brew install git python@3.12 gh ripgrep actionlint shellcheck node jq
brew install --cask dotnet-sdk docker
open -a Docker   # start the daemon once
```

### Ubuntu / Debian (incl. WSL2 and most agent containers)

```bash
sudo apt-get update
sudo apt-get install -y git python3 ripgrep shellcheck jq curl rsync docker.io nodejs
# .NET 8 SDK
sudo apt-get install -y dotnet-sdk-8.0 || {
  curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0
  echo 'export PATH="$HOME/.dotnet:$PATH"' >> ~/.bashrc
}
# GitHub CLI
(type -p wget >/dev/null || sudo apt-get install -y wget) && \
  sudo mkdir -p -m 755 /etc/apt/keyrings && \
  wget -qO- https://cli.github.com/packages/githubcli-archive-keyring.gpg | sudo tee /etc/apt/keyrings/githubcli-archive-keyring.gpg >/dev/null && \
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" | \
  sudo tee /etc/apt/sources.list.d/github-cli.list >/dev/null && \
  sudo apt-get update && sudo apt-get install -y gh
# actionlint
bash <(curl -sSfL https://raw.githubusercontent.com/rhysd/actionlint/v1.7.12/scripts/download-actionlint.bash) 1.7.12 ~/.local/bin
sudo usermod -aG docker "$USER"   # then log out/in so docker runs without sudo
```

### Windows

Use WSL2 (Ubuntu) and follow the Ubuntu steps; the scripts are bash. Native Windows works for
`dotnet format` and Unity only. Unity for Windows: Unity Hub → Installs → 2021.3.45f2.

### Environments without Docker (some cloud agent sandboxes)

The shadow build needs Unity's managed DLLs once. Any of:

- `UNITY_PATH=<Unity editor binary>`: the DLLs are read from that install;
- `UNITY_DLLS=<dir containing UnityEngine.dll>`: e.g. a copy of `.tools/unity-managed/<ver>` from
  a machine that has Docker (the directory is not committed: Unity's DLLs are not
  redistributable). The `Tests/` compile also needs that copy's `packages/` subdirectory, which
  holds the NUnit and Test Framework tarballs. Without it, pass `--no-tests` to `shadow_build.py`
  and say so in the PR body;
- without either, run only `python3 "Tools~/ci/check_repo.py"` and the format check, and let PR
  CI run the shadow build. Say so in the PR body.

## One-time setup per clone

```bash
git config core.hooksPath .githooks          # pre-commit: invariants, format, shadow build
"Tools~/ci/fetch-unity-dlls.sh"              # Unity managed DLLs + test packages -> .tools/unity-managed/<ver>
"Tools~/ci/check.sh" --full                  # should print "check.sh: OK"
gh auth login                                # repo + project scopes for agent workflows
```

Optional, for local Unity tests:

```bash
# Unity Hub CLI (macOS path shown); installs the editor without the Hub UI.
"/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" -- --headless install --version 2021.3.45f2 --changeset <changeset>
export UNITY_PATH="/Applications/Unity/Hub/Editor/2021.3.45f2/Unity.app/Contents/MacOS/Unity"
"Tools~/unity/run-tests.sh" --platform EditMode --filter 'TurboHTTP.Tests.Core'
```

Unity needs an activated license (Personal is fine) in the Hub once.

## Daily commands

```bash
"Tools~/ci/check.sh"               # before every commit/push (~10 s warm)
"Tools~/ci/check.sh" --fix         # apply whitespace formatting
"Tools~/ci/check.sh" --full        # before opening a PR
"Tools~/unity/run-tests.sh" --platform PlayMode --filter 'Http2'   # scoped Unity tests
```

### Analyzer ratchet

Every analyzer warning in TurboHTTP sources fails the shadow build unless it is listed in
`Tools~/ci/analyzer-baseline.txt` (`rule|file|source line`, so line moves don't matter). `Tests/` is
outside the ratchet: test projects build without analyzers and only their compile errors fail.

- New warning: fix it. The rule's reason is in `.editorconfig` or `BannedSymbols.txt`.
- Deliberate exception (rare): run `python3 "Tools~/ci/shadow_build.py" --variant all --update-baseline`
  and explain the new baseline line in the PR.
- Fixed an existing one: `--update-baseline` shrinks the file; commit it.
- Vendored code (`Runtime/Transport/BouncyCastle/Lib/**`) is compiled but never analyzed or formatted.

### Adding files without Unity

New `.cs`/`.md`/`.json` files and folders under Unity-imported paths need a `.meta`:

```bash
python3 "Tools~/ci/gen_meta.py" Runtime/Core/NewType.cs Runtime/Core/NewFolder
```

Plugins, textures, asmdefs and other importer-specific assets should get their `.meta` from Unity.
Dev tooling lives in `Tools~/` and `.github/` (Unity ignores `~` and hidden folders, so they need
no `.meta` and do not ship).

## CI configuration (repository admin)

### Unity license

None. CI has no `UNITY_LICENSE`, `UNITY_EMAIL` or `UNITY_PASSWORD` secret and workflows must not
reference them: Unity runs only in a licensed host editor (owner decision 2026-11-02, #188, #100).
Former CI roles: the EditMode/PlayMode matrix is `run-tests.sh`, the nightly internet run is
`run-tests.sh --category ExternalNetwork` (the `nightly-external.yml` workflow was a no-op without a
license and is gone, #263), the IL2CPP player builds are `build-player.sh` (#273). A 2021.3 plus
latest-LTS matrix and coverage artifacts are not planned.

### `main` ruleset

Ruleset "main protection" on the default branch (bypass: repository admins):

- no deletion, no force-push;
- pull request required (0 approvals; stale approvals dismissed on push);
- required status check `ci`, branch must be up to date;
- code scanning: CodeQL, block on errors and high+ security alerts;
- Copilot code review on push; code quality errors.

Re-create or inspect it with:

```bash
gh api repos/fatal10110/turboHTTP/rulesets --jq '.[] | "\(.id) \(.name) \(.enforcement)"'
```

### Dependabot

`.github/dependabot.yml` updates GitHub Actions weekly and the shadow-build analyzer package
(`Tools~/ci/Shadow.props`) monthly.

## Unity version bump

1. Change `Tools~/ci/unity-version`.
2. `"Tools~/ci/fetch-unity-dlls.sh"` (CI refetches automatically: the cache key includes the version).
3. Check that the `unityci/editor:ubuntu-<ver>-base-3.2.2` tag exists; update the tag suffix in
   `fetch-unity-dlls.sh` if GameCI moved on.
4. Re-run `"Tools~/ci/check.sh" --full`; update `COMMON_DEFINES` in `shadow_build.py` if the new
   Unity defines more `UNITY_*_OR_NEWER` symbols.

## UniTask pin

The UniTask adapters (`TurboHTTP.UniTask`, `TurboHTTP.UniTask.WebSocket`, `TurboHTTP.Tests.UniTask`)
are compiled against one UniTask release, from the Cysharp git repository (owner decision on #311,
2026-11-04: the git URL customers use, pinned by tag and commit; no OpenUPM, never UniTask source in
this repository). `TAG` and `SHA` in `Tools~/ci/unitask_pin.py` are the only copy of the pin:

- the shadow build fetches exactly `SHA` (git checks the object ids) and, on the first fetch, checks
  that `TAG` still points at it;
- `'./Tools~/unity/run-tests.sh' --with-unitask` installs
  `https://github.com/Cysharp/UniTask.git?path=src/UniTask/Assets/Plugins/UniTask#<TAG>` and exits 2
  when `Packages/packages-lock.json` resolved another commit. `--with-unitask=local` (an editor host
  without git) installs a copy of the shadow build's verified checkout of `SHA` as a `file:` package
  instead, and the lock must name that copy.

To bump:

1. `git ls-remote https://github.com/Cysharp/UniTask.git 'refs/tags/<tag>' 'refs/tags/<tag>^{}'`; the
   commit is the `^{}` line for an annotated tag, else the only line.
2. Change `TAG` and `SHA` together in `Tools~/ci/unitask_pin.py`.
3. `"Tools~/ci/check.sh"` (fetches the new commit into `.tools/unitask/<sha>/`; fix any adapter
   compile error, remembering that the build has Unity 2021.3's defines: no `UNITASK_NETCORE`, no
   `UNITY_2022_3_OR_NEWER`).
4. Host editor: `'./Tools~/unity/run-tests.sh' --platform PlayMode --with-unitask --filter
   'UniTaskCancellationTests'` (`testing.md`, "Running Unity tests"); `--with-unitask=local` when the
   editor host has no git.
5. Update the version customers are told to install in `Documentation~/Modules/UniTask.md` if it names one.
