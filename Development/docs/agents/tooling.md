# Tooling: install, configure, verify

Everything a human or an agent needs to run TurboHTTP's quality gates locally, and how CI and
the `main` ruleset enforce them. Applies to every environment (macOS, Linux, Windows/WSL,
cloud agent containers).

## What runs where

| Gate | Local command | CI job | Blocks merge |
| --- | --- | --- | --- |
| Repo invariants: every imported asset has a `.meta`, no orphan/duplicate GUIDs, asmdef dependency graph follows `architecture.md`, JSON/asmdef/package.json valid | `python3 "Tools~/ci/check_repo.py"` | `lint` | yes |
| Whitespace format (`.editorconfig`, `dotnet format whitespace`) | `"Tools~/ci/check.sh"` (`--fix` to apply) | `lint` | yes |
| Shadow build: every runtime/editor asmdef compiled by the .NET SDK for the Editor, iOS and Android define sets, with Roslyn analyzers and banned APIs (`Tools~/ci/BannedSymbols.txt`), warnings ratcheted by `Tools~/ci/analyzer-baseline.txt` | `"Tools~/ci/check.sh"` (editor) / `--full` (all) | `lint` | yes |
| actionlint, shellcheck, Python tool tests, issue-seed validation | `"Tools~/ci/check.sh" --full` | `lint` | yes |
| Unity Test Runner, EditMode + PlayMode (Unity 2021.3.45f2) | `"Tools~/unity/run-tests.sh" --platform EditMode\|PlayMode [--filter X]` | `unity-tests` | yes, once the license secrets exist |
| CodeQL (C#, build-mode none; GitHub Actions) | — | `CodeQL` | errors / high+ security alerts |
| Copilot code review, code quality | — | ruleset | advisory / errors |

The required status check is the aggregate job `ci` (`.github/workflows/ci.yml`), which passes
only when `lint` and `unity-tests` pass.

Why a shadow build: Unity compiles the package itself, but CI and agents need compiler errors
and analyzers without a Unity install or license. `Tools~/ci/shadow_build.py` generates one
`.csproj` per asmdef (references, `allowUnsafeCode`, `noEngineReferences`, define constraints,
include/exclude platforms) under `.build/shadow/<variant>/`, references Unity's managed DLLs,
and mirrors Unity 2021.3's compiler setup (.NET Standard 2.1, C# 9, `NETSTANDARD2_1` without the
SDK's `*_OR_GREATER` symbols). Test assemblies are not part of it (they need Unity's
test-framework package); they compile in the `unity-tests` job.

## Required tools

| Tool | Version | Used by |
| --- | --- | --- |
| git | any recent | everything |
| Python | 3.9+ (stdlib only) | `check_repo.py`, `shadow_build.py`, agent tools, issue seed |
| .NET SDK | 8.0.x | `dotnet format`, shadow build |
| Docker | any (daemon running) | `fetch-unity-dlls.sh` (one-time, pulls `unityci/editor:ubuntu-<ver>-base-3.2.2`, ~4 GB, keeps 34 MB) |
| GitHub CLI `gh` | 2.40+ | agent workflows, issue seed, PR handling |
| ripgrep `rg` | any | agent tools |
| actionlint | 1.7+ | `check.sh --full` |
| shellcheck | 0.9+ | `check.sh --full` |
| Node.js | 18+ | `turbohttp-waves` simulation (`waves_sim.mjs`) |
| Unity | 2021.3.45f2 (optional locally) | `run-tests.sh`; without it Unity tests run on PR CI only |

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
  redistributable);
- without either, run only `python3 "Tools~/ci/check_repo.py"` and the format check, and let PR
  CI run the shadow build. Say so in the PR body.

## One-time setup per clone

```bash
git config core.hooksPath .githooks          # pre-commit: invariants, format, shadow build
"Tools~/ci/fetch-unity-dlls.sh"              # Unity managed DLLs -> .tools/unity-managed/<ver>
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
`Tools~/ci/analyzer-baseline.txt` (`rule|file|source line`, so line moves don't matter).

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

### Secrets for `unity-tests`

GameCI activation (<https://game.ci/docs/github/activation>):

1. Activate Unity locally once (Hub → sign in → Personal license).
2. Copy the license file: macOS `/Library/Application Support/Unity/Unity_lic.ulf`, Windows
   `C:\ProgramData\Unity\Unity_lic.ulf`, Linux `~/.local/share/unity3d/Unity/Unity_lic.ulf`.
3. Add repository secrets (Settings → Secrets and variables → Actions):
   `UNITY_LICENSE` (file contents), `UNITY_EMAIL`, `UNITY_PASSWORD`.

```bash
gh secret set UNITY_LICENSE < "/Library/Application Support/Unity/Unity_lic.ulf"
gh secret set UNITY_EMAIL
gh secret set UNITY_PASSWORD
```

Until these exist the `unity-tests` job warns and passes.

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
