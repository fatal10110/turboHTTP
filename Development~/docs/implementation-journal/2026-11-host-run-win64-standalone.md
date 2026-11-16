# 2026-11 Win64 standalone player: first complete unfiltered suite runs (#1219)

Test-run record for #1219. It sets the `Player-StandaloneWindows64` skip and min-total entries in
`Tools~/unity/skip-baseline.txt`. Setup: 2026-11-16, Unity 2021.3.45f2, Windows host editor started
from WSL2, one Unity process at a time (`flock /tmp/turbohttp-unity.lock`). The test player was built
by `run-tests.sh --platform StandaloneWindows64` at Stripping Disabled. Since #1236, a not-selected
`[Explicit]` test is not counted against the skip baseline.

## Commands

```
flock /tmp/turbohttp-unity.lock './Tools~/unity/run-tests.sh' --platform StandaloneWindows64 --backend Mono
flock /tmp/turbohttp-unity.lock './Tools~/unity/run-tests.sh' --platform StandaloneWindows64 --backend IL2CPP
```

## Results

| Run | Commit | Backend | Total | Passed | Failed | Skipped (counted) | Exit |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Mono 1 | main 5ab19c6 | Mono | 3602 | 3593 | 2 | 7 (5) | 1 |
| Mono 2 | c8071f6 (#1243: 5ab19c6 plus the TestTempPath fix) | Mono | 3603 | 3596 | 0 | 7 (5) | 0 |
| IL2CPP | 5ab19c6 | IL2CPP | - | - | - | - | 3 (no results) |

- Mono 1 exited 1 for three reasons: the two failures below, and the skip and total gates, which
  failed because the baseline had no `Player-StandaloneWindows64` entries yet.
- Mono 2 passed both gates: `skipped=5 within baseline 5 (2 not-selected [Explicit] not counted)`
  and `total=3603 reaches min-total 3603`.
- The five counted skips are all `[env]`, listed with their reasons in the skip-baseline header.
  The two not-counted cases are the `[Explicit]` Loop384 cases.

## Findings

- Mono 1 failures: `FileDownloaderTests.Download_WritesBodyToFile` and
  `FileDownloaderTests.Download_BasePath_MixedCaseBehavior_IsPlatformAware`. Each expected
  `C:/Users/...` and got `C:\Users\...`. In a player, the test root from `TestTempPath` is
  `Application.temporaryCachePath`, which uses `/` separators on Windows. `FileDownloader` returns
  `Path.GetFullPath`, which uses `\`. The product behaviour is correct. The test helper was wrong:
  `TestTempPath.Resolve()` now returns the root in the platform's full-path form (`Path.GetFullPath`,
  player branch only). The new test `TestTempPathTests.Root_IsInThePlatformsFullPathForm` covers it,
  which adds one case (3602 -> 3603).
- The IL2CPP build failed before any test ran: "Could not set up a toolchain for Architecture x64.
  Make sure you have the right build tools installed for il2cpp builds" (VS 2022+ with C++ compilers
  and Windows 10 SDK). The host has no `Windows Kits\10` and no MSVC `VC` directory. Tracked in #1225.

## Not run

- Win64 IL2CPP suite: blocked by #1225. When it first runs, it must confirm the Mono numbers. If its
  numbers differ, the baseline keeps the larger skip count and the smaller total. #1219 stays open
  for this row.
- Editor EditMode/PlayMode suites: only the scoped
  `--filter 'TestTempPathTests|FileDownloaderTests'` runs (19/19 pass, Editor PlayMode and the Win64
  Mono player). The full suites run in the periodic host-editor run.
