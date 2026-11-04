# 2026-11 iOS handoff folder from local configuration (#99)

## What
- `Tools~/unity/build-ios-test-player.sh` has no built-in handoff path any more. The Xcode project goes
  to `--out`, or `UNITY_IOS_HANDOFF_OUT` when `--out` is not given; with neither the script exits 2
  before doing any work. The usage header uses a placeholder instead of a machine's editor path.
- The value must be the `xcode/` folder inside the handoff folder (`<handoff>/xcode`). Any other last
  path component exits 2, because the script removes `READY`, `DONE` and `out/` in the parent of that
  folder and `rsync --delete`s into it: a value pointing at the handoff folder itself would delete an
  unrelated `out/` one level up and wipe the Mac's results (review on PR 348).
- `Development~/docs/agents/testing.md` step 5: the handoff folder is local, set
  `UNITY_IOS_HANDOFF_OUT=<handoff>/xcode`, keep machine paths out of the repo, issues and PRs.

## Files
- `Tools~/unity/build-ios-test-player.sh`
- `Development~/docs/agents/testing.md`

## Decisions
- Required rather than defaulted: a default would either be machine-specific or point somewhere the Mac
  cannot see.
- Variable name `UNITY_IOS_HANDOFF_OUT`, next to `UNITY_IOS_TEST_PROJECT` / `UNITY_IOS_PACKAGE_COPY`;
  `TURBOHTTP_IOS_OUT` stays internal (Unity's staging output). It keeps `--out` semantics (the `xcode/`
  folder) rather than naming the handoff folder, so `--out` and the variable mean the same thing; the
  basename guard rejects the likely mistake.
- Local paths already posted in issue comments (#99, #293, #322, #344) were replaced with "kept locally"
  wording.

## Deferrals
- Machine paths in the sibling scripts' usage headers and in older docs (`build-player.sh`,
  `run-samples.sh`, `run-tests.sh`, `run-device-tests.sh`, `run-emulator-tests.sh`,
  `how-to-run-tests-with-temp-unity-project.md`, `Development~/scripts/phase19/run-allocation-gate.sh`):
  pre-existing, tracked in a follow-up issue linked from PR 348.

## Verification
- `'./Tools~/ci/check.sh'` (including shellcheck).
- Script exercised without Unity: no setting, `--out ""` and a value whose last component is not
  `xcode` (`--out <h>` and the env variable) all exit 2 with the usage message before any filesystem
  change; `<h>/xcode` and `<h>/xcode/` from either source pass the check and stop at
  `UNITY_UNAVAILABLE` (exit 3).

## Not run
- Unity EditMode/PlayMode, IL2CPP, device: no package code changed.
- End-to-end simulator handoff (build, copy, `READY`, Mac run) after this change.
