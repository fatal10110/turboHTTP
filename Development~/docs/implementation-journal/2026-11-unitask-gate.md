# 2026-11: UniTask adapter gate (#311)

## What

Nothing compiled `TurboHTTP.UniTask`, `TurboHTTP.UniTask.WebSocket` or `TurboHTTP.Tests.UniTask`.
The shadow build skipped them because `TURBOHTTP_UNITASK` was not defined, CI has no Unity (#188),
and the Unity test project never installed `com.cysharp.unitask`. #313 had to find two compile errors
in the adapters by hand. Two gates now compile them, both from one pin (owner decision on #311,
2026-11-04: option A, the Cysharp git repository at tag `2.5.11`, commit
`2e993ff18f28c931602a07292df0b0804eebef99`; UniTask source is never committed).

- **Pin.** `Tools~/ci/unitask_pin.py` holds `TAG` and `SHA`, builds the manifest URL, checks a
  `packages-lock.json` and a results file, and fetches the source. The fetch is a sparse, shallow
  `git fetch` of the SHA into `.tools/unitask/<sha>/`. It checks that `FETCH_HEAD` and `HEAD` are the
  SHA (git verifies object ids), and on the first fetch it checks that the tag still points at the SHA.
- **Shadow build.** `shadow_build.py` fetches UniTask whenever it builds the tests and adds UniTask's
  runtime asmdef (`UniTask`) as an external assembly: no analyzers, outside the ratchet. `Linq` and
  `External/*` have their own asmdefs and are not built. A new per-asmdef `versionDefines` evaluation
  (`asmdef_defines`, `version_matches`) gives `TURBOHTTP_UNITASK` to the three asmdefs that declare
  it, as Unity does, rather than a global define. `--no-tests` (interop, soak, fuzz, Autobahn) never
  fetches. `--no-unitask` is the offline escape and prints that the adapters were skipped.
  `check.sh`'s `dotnet format` now excludes `.tools/` (UniTask's own source does not follow our
  `.editorconfig`).
- **Unity gate.** `run-tests.sh --with-unitask` adds the git URL to the throwaway project's manifest.
  After Unity runs, it fails with exit 2 unless `Packages/packages-lock.json` resolved the pinned
  commit, and with exit 4 when no `TurboHTTP.Tests.UniTaskAdapter` test ran. Without the flag the
  manifest entry is removed, so later runs are unchanged.
- **Local mode (review round 1).** The Windows host editor has no git, so Unity cannot resolve any git
  package ("No 'git' executable was found"). Installing Git on the host is the owner's call, not this
  lane's. `--with-unitask=local` installs the same pinned commit from the checkout `unitask_pin.fetch()`
  verified (git in WSL, as for the shadow build), rsynced to `%LOCALAPPDATA%/turbohttp/unitask-<sha>`
  and referenced as `file:`. `check-lock` then requires `source: local` and that path. The git URL
  stays the default and the documented customer install; when its lock check fails and the Unity log
  names the missing git, `run-tests.sh` prints the hint. `check.sh --no-unitask` passes through to the
  shadow build (offline use).

## Files

- `Tools~/ci/unitask_pin.py` (new), `Tools~/ci/test_unitask_pin.py` (new; in `check.sh --full`)
- `Tools~/ci/shadow_build.py`, `Tools~/ci/check.sh`, `Tools~/unity/run-tests.sh`
- `Development~/docs/agents/testing.md` ("Running Unity tests", host-editor gate list),
  `Development~/docs/agents/tooling.md` (shadow-build bullets, new "UniTask pin" bump section)
- `Documentation~/Modules/UniTask.md`: only the internal `snippet: skip` reason, which had become false

## Decisions

- **The pin lives in Python, not in a data file.** `run-tests.sh` reads it through
  `unitask_pin.py url` and `check-lock`, so the tag and SHA are written in exactly one place.
- **git, not the codeload tarball.** git checks every object id against its content, so `HEAD == SHA`
  verifies the files themselves. A tarball's commit comment would only be a claim. The sparse fetch
  is about 4 MB and takes under a second.
- **Fail, not skip, when the fetch fails.** A silent skip would bring the original gap back.
  Offline builds opt out explicitly with `--no-unitask`.
- **Per-asmdef versionDefines.** A global `TURBOHTTP_UNITASK` would leak into assemblies that do not
  declare it. Evaluating `versionDefines` the way Unity does keeps the shadow build faithful. They
  are the only `versionDefines` in the package. UniTask's own `com.unity.modules.*` versionDefines
  are not applied: its module-specific code is not ours to gate.
- **Documentation snippets stay skipped.** `Documentation~/Modules/UniTask.md` blocks keep
  `snippet: skip`. Compiling them would make the documentation build depend on the network, which
  `--no-unitask` must not do. Only the reason text changed.
- **Results check.** The `--with-unitask` gate requires at least one adapter test to run, because a
  vacuous filtered pass is otherwise invisible: `summarize_results.py` does not count discovered tests.

## How verified

- `shadow_build.py --variant editor` before the change: the three UniTask asmdefs are absent (skipped).
  After it: 33 assemblies, including `UniTask`, `TurboHTTP.UniTask`, `TurboHTTP.UniTask.WebSocket` and
  `TurboHTTP.Tests.UniTask`, with 0 errors. The ios and android variants build 30 each.
- Regression proof: a probe file in `Runtime/UniTask` that uses the bare `UniTask` type name (one of
  the #313 errors) fails the shadow build with CS0118. The probe was removed afterwards.
- `Tools~/ci/test_unitask_pin.py`: 16 tests, no network. They cover the fetch from a local `file://`
  git remote (cached reuse, moved tag, unknown commit, short SHA), the lock check (pinned, other
  commit, missing entry, registry source, local copy, CLI exit codes), the results count, the version
  expressions and `generate()` with and without the package.
- `check.sh --full`: OK (recorded with the pushed head in the PR body, "Local gates").
- Host editor (Unity 2021.3.45f2, Windows host, serialized under the Unity lock), on snapshot 0c88d95:
  - `run-tests.sh --platform PlayMode --with-unitask=local --filter 'UniTaskCancellationTests'`: exit 0,
    6 of 6 adapter tests passed. `TurboHTTP.UniTask`, `TurboHTTP.UniTask.WebSocket` and
    `TurboHTTP.Tests.UniTask` compiled. The lock check confirmed the local copy of 2e993ff.
  - The same with `--platform EditMode`: exit 4, 0 adapter tests (they are PlayMode tests).
  - `--with-unitask` (git URL): exit 2. Unity: "No 'git' executable was found"; the hint printed.
  - No flag: the manifest has no `com.cysharp.unitask`, exit 0.
- The git-URL resolution itself (lock `source: git`, `hash` == SHA) has not run in a Unity editor,
  because no host here has git for Unity. Its check is covered by the unit tests only.

## Not run

- IL2CPP players and devices with UniTask are not run. The adapters have no platform-specific code,
  and the ios/android shadow variants compile them.
- A full PlayMode suite with `--with-unitask`: only the scoped `UniTaskCancellationTests` run.
