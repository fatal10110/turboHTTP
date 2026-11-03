# 2026-11 Release layout: `Development~` rename and release process (#6, #101)

Waves run w1102, lane `release-layout` (last lane of the run, alone).

## What

- **#6: internal docs are no longer imported.** `git mv Development Development~` (427 tracked files, no
  tracked `.meta` under it). Unity skips `~` folders, so customer projects no longer get the reviews, plans and
  journals. Every live path reference moved in the same change.
  - `CLAUDE.md`: the workspace table, the guide links and the journal rule. The journal no longer needs a
    `.meta`, because the folder is hidden.
  - Skills: `turbohttp-issue`, `turbohttp-pr-review` and `turbohttp-waves` (`SKILL.md`, and the `waves.js`
    prompts, which now name `Development~` among the paths to single-quote).
  - `Tools~/ci/check_repo.py`: `META_EXEMPT_TOP` is now empty, and the docstring and messages are updated.
  - Other tooling and docs: `Tools~/ci/check.sh`, `.github/workflows/ci.yml`, `.github/issue-seed/SCHEMA.md`,
    `CHANGELOG.md`, `README.md` (the contributor docs link) and the comment in `TlsNegativeSuiteTests.cs`.
  - Live docs inside `Development~/` (overview, README, high-level, agents guides, phase docs): about 24 files.
  - `.gitignore`: the three `Development.meta` / `Development/**/*.meta` lines are removed.
  - `Tools~/unity/build-player.sh`: the rsync now excludes `Development~/` instead of `Development/`.
  - `Tools~/agent-tools/issue_slice.py`: the path regex accepts `Development~?`, because old issue bodies still
    name `Development/...`.
- **#6: OpenUPM tarball contents.** The new `.npmignore` keeps `Development~/`, `Tools~/`, `.claude/`,
  `.github/`, `.githooks/` and `.config/` out of the `npm pack` tarball that OpenUPM builds.
  `npm pack --dry-run` lists 2880 files: `Runtime`, `Editor`, `Plugins`, `Tests`, `Samples~`, `Documentation~`,
  `package.json` and the root docs and notices with their `.meta` files.
- **#101: release process.** All of it is new.
  - `Development~/docs/release-checklist.md`: distribution, semver policy (`v1.0.0-pre.N` until GA, never
    re-tag), and the pre-tag checklist (scope, notices refresh #5, `check.sh --full`, the full host-editor run
    with player builds and samples, #99 for GA, the version bump PR). It also covers tag cutting and the
    post-tag install check and OpenUPM check.
  - `Development~/docs/openupm.md`: requirements, the submission form or the `openupm/openupm` PR with a
    `data/packages/com.turbohttp.complete.yml` sketch, and user install.
  - `.github/workflows/release.yml`: runs on a `v*.*.*` tag. It checks that the tag is an ancestor of
    `origin/main`, extracts the notes with `Tools~/ci/release_notes.py` (which fails when the tag is not
    `v<semver>`, differs from `package.json`, or has no non-empty CHANGELOG section), and runs
    `gh release create --verify-tag`, adding `--prerelease` when the tag contains `-`.
  - README "Installation" now covers a Git URL with a tag (UI and `manifest.json`), OpenUPM ("once listed"; CLI
    or scoped registry) and disk install.
  - The CHANGELOG `1.0.0-pre.1` section has a distribution line.
  - Pointers: the phase-17 "superseded in part" note, the roadmap entry, the `project-status.md` row 17 and a
    `tooling.md` "What runs where" row.

## Decisions

- **History left unchanged.** `Development~/docs/implementation-journal/**` (82 files) and
  `.github/issue-seed/issues/**` keep `Development/...`, because they are records of past states. These dated
  reviews describe the pre-rename layout and also stay as they are:
  - `review-2026-10-full-library-review.md:125`
  - `review-2026-10-30-state-plan-tracker.md:12,15,83`
  - the `architecture.md:140` sentence "the old `Development/docs/architecture.md` was removed"
- **Location of the release docs.** They live under `Development~/docs/`: they are for maintainers, are not
  shipped and need no `.meta`. `Documentation~` is customer-facing, and the README carries the user part.
- **No package.json/CHANGELOG invariant in `check_repo.py`.** The Release workflow and
  `test_release_notes.py` (`test_repository_changelog_has_section_for_package_version`, run by
  `check.sh --full` in CI) already fail when the CHANGELOG has no section for the current version.
- **No extra release artifact.** For a UPM Git URL package the tag is the artifact. GitHub attaches the source
  archives, and OpenUPM builds from the tag.
- **`run-tests.sh` / `run-samples.sh`.** They copy the repo into a Unity project, and Unity skips `Development~`
  there, so no exclude was added.

## Deferred (owner actions, not code)

- Cutting the first tag `v1.0.0-pre.1` and the first Release workflow run.
- The OpenUPM submission (form or openupm/openupm PR).
- #99 device-matrix sign-off, needed for 1.0.0 GA.
- The Asset Store (free or paid), a post-1.0 decision per the 2026-11-02 comment on #101.

## Verification

- `python3 -m unittest discover -s 'Tools~/agent-tools' -p 'test_*.py'`: new tests in
  `test_release_notes.py` (9) and `test_check_repo_meta_scope.py` (3). `IssueSlice` asserts both path
  spellings. The meta-scope test fails against `origin/main`'s `check_repo.py`
  (`META_EXEMPT_TOP == {"Development"}`).
- `'./Tools~/ci/check.sh' --full`, with actionlint and shellcheck installed locally, which covers `release.yml`.
  waves_sim is included.
- `npm pack --dry-run` checked the contents of the OpenUPM tarball.
- An `rg` check found no live `Development/` path outside the history files above.
- Not run: Unity (no Unity code changed; the only C# edit is a doc comment), a real tag push or Release
  workflow run, and an OpenUPM build.
