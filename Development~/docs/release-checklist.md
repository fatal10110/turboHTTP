# Release checklist

How TurboHTTP versions are numbered, distributed and cut (#101, #6). Follow it for every tag.

## Distribution

TurboHTTP is an open-source UPM package (owner decision 2026-11-02, #6, #4). Users install it in two ways:

- **Git URL with a tag:** `https://github.com/fatal10110/turboHTTP.git#vX.Y.Z`. Unity clones the repository at
  that tag and imports the package root. It skips folders ending in `~` (`Development~`, `Tools~`, and
  `Samples~`/`Documentation~`, which the Package Manager exposes as samples and docs) and dot folders
  (`.claude`, `.github`, `.githooks`, `.config`).
- **OpenUPM:** `com.turbohttp.complete` on `https://package.openupm.com`, built by OpenUPM from the same tags
  (`npm pack`, filtered by `.npmignore`). Submitting the package is described in [`openupm.md`](openupm.md).

What a user receives is `Runtime/`, `Editor/` (customer-facing build hooks and settings), `Plugins/`, `Tests/`,
`Samples~/`, `Documentation~/`, `package.json`, `README.md`, `CHANGELOG.md`, `LICENSE.md`,
`Third-Party Notices.md`, and the public contributor files `AGENTS.md`, `CLAUDE.md` and `CONTRIBUTING.md`.
Internal material (`Development~/`, `Tools~/`, `.claude/`, `.github/`) is never imported. Check the OpenUPM
contents with `npm pack --dry-run` from the repository root.

There is no `.unitypackage` and no Asset Store listing for v1.0. Whether to list on the Asset Store
(free or paid) is a post-1.0 decision. The old Asset Store tasks in
[`phases/phase-17-release.md`](phases/phase-17-release.md) (17.3, 17.6) are superseded by this document.

## Versioning

[Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html), with
[Keep a Changelog](https://keepachangelog.com/en/1.0.0/) sections in `CHANGELOG.md`.

- Tags are `v<package.json version>`: `v1.0.0-pre.1`, `v1.0.0-pre.2`, …, `v1.0.0`, `v1.0.1`, `v1.1.0`.
  `.github/workflows/release.yml` rejects any other shape.
- **Until 1.0.0 GA**, every release is `1.0.0-pre.N`, cut from `main` while the physical-device matrix (#99)
  waits for hardware. A tag with `-` is published as a GitHub pre-release.
- **1.0.0** needs the #99 device sign-off and a green full host-editor run on the release commit.
- **After GA:** patch for fixes, minor for backward-compatible features, major for breaking changes.
  Removing or changing public API is a major release (record it under `### Breaking`).
- Never move, delete or re-push a published tag. Git URL users and OpenUPM pin tags; a bad release is
  fixed by the next version.

## Before the tag

Work from a clean worktree of the commit you will tag (normally `origin/main`).

1. **Scope.** Every issue in the release's milestone is closed or moved, and `main` is green on the
   required `ci` check.
2. **Third-party notices (#5).** `Third-Party Notices.md` and `Runtime/Transport/BouncyCastle/README.md`
   name the same bc-csharp upstream commit and strip commit as the vendored `Runtime/Transport/BouncyCastle/Lib/`.
   Refresh both in the same PR as any BouncyCastle re-sync, and add a notice for any newly vendored code.
   `LICENSE.md` stays MIT (#4).
3. **Repository gates.** `'./Tools~/ci/check.sh' --full` passes.
4. **Host-editor full run.** All the steps in [`agents/testing.md`](agents/testing.md), "Host-editor gates", run on the release
   commit: full EditMode and PlayMode suites with the skip baseline, the `ExternalNetwork` run, and the
   player builds.

   ```bash
   export UNITY_PATH=<Unity 2021.3.45f2 editor binary>
   './Tools~/unity/run-tests.sh' --platform EditMode
   './Tools~/unity/run-tests.sh' --platform PlayMode
   './Tools~/unity/run-tests.sh' --platform PlayMode --category ExternalNetwork
   './Tools~/unity/build-player.sh' --target win64-mono
   './Tools~/unity/build-player.sh' --target android-il2cpp
   './Tools~/unity/build-player.sh' --target android-il2cpp --bouncycastle
   './Tools~/unity/run-samples.sh'
   ```

   Record the results (commit, pass and skip counts, build sizes) in the release PR.
5. **Device matrix (GA only).** #99 is signed off. Pre-releases list it as pending in the release notes.
6. **Version bump PR.** In one PR against `main`:
   - `package.json` `version` is the new version.
   - `CHANGELOG.md` has a `## [X.Y.Z] - YYYY-MM-DD` section with the release date. Its entries come from
     `## [Unreleased]`, if that section exists. The section is what the GitHub Release shows, so write it
     for users.
   - `README.md` "Installation" names the new tag in its Git URL examples.
   - Preview the release notes. This command also checks that the tag matches `package.json`:
     `python3 'Tools~/ci/release_notes.py' vX.Y.Z`.

## Cut the release

7. Tag the merge commit of the version bump PR on `main` and push the tag:

   ```bash
   git fetch origin main
   git tag -a vX.Y.Z -m "TurboHTTP X.Y.Z" origin/main
   git push origin vX.Y.Z
   ```

8. The **Release** workflow (`.github/workflows/release.yml`) checks that the tag is on `main`, takes the
   notes from the CHANGELOG section, and creates the GitHub Release (a pre-release when the tag contains `-`).
   If it fails, fix the cause on `main` and release the next version. Do not re-tag.

## After the tag

9. **Install check.** In a new Unity 2021.3 project, add `https://github.com/fatal10110/turboHTTP.git#vX.Y.Z`
   through **Add package from git URL...**. It should compile with no console errors. Import one sample.
10. **OpenUPM.** Once the package is listed, OpenUPM builds new tags on its own. Check the build status on
    `https://openupm.com/packages/com.turbohttp.complete/`. A failed build there needs a new version.
11. **Announce.** Link the GitHub Release from the milestone or tracking issue, and close the milestone when
    it is complete.
