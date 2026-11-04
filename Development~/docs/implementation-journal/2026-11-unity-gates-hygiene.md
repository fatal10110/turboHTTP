# 2026-11 Unity gates hygiene: player skip baselines, WSL interop retries (#365, #355)

## What

- **#365, skip baselines.** `Tools~/unity/skip-baseline.txt`: `Player-iOS` 18 -> 21. iOS simulator run 7 (#99:
  main 2ec74e2, iOS 26.4, Unity 6000.3.26f1, IL2CPP High) skips 21 tests, all `[env]`. The 3 new skips are the
  "abandoned read faults later" tests from #346/#357. Like the 2 older fault-observed cases, they skip on iOS with
  "This runtime did not report a known unobserved faulted task.":
  - `BodyReadCancellationTests.BodyRead_AbandonedReadFaultsLater_FaultIsObserved`
  - `HeadReadCancellationTests.EnsureBuffered_AbandonedReadFaultsLater_FaultIsObserved`
  - `WebSocketHandshakeReadCancellationTests.Upgrade_AbandonedReadFaultsLater_FaultIsObserved`

  The file now lists all 5 with that reason. `Player-Android` stays 16. One unfiltered API 29 emulator run on main
  e0a5bf7, which includes #346 and #357, gave 2628 cases, 0 failed and 16 skipped: exactly the 16 listed entries.
  All 5 fault-observed tests ran and passed there, and so did `BouncyCastleHandshakeAbandonTests`. That class uses
  the same skip reason but did not skip on Android, and it is not in the run 7 iOS skip list either.
- **#355, WSL interop outages.** New sourced helper `Tools~/unity/wsl-interop.sh`:
  - `wsl_resolve_localappdata`: a `cmd.exe` lookup counts only when it answered; it is retried with back-off.
  - `wsl_unity_launch`: an attempt that wrote no log never started. From WSL it is retried with back-off; on other
    hosts it gets one attempt and then exits 2.
  - `wsl_interop_up`: one `cmd.exe /c echo` probe that tells an interop outage from a Windows tool's own error.
  - `wsl_interop_unavailable` / `wsl_exit_if_interop`: print "<script>: WSL interop unavailable: ..." and exit **5**.

  The back-off copies `win_emulator_procs` (#353): pause `wsl_poll_s*try*2` s, capped at 30 s, for `wsl_tries=8` tries,
  about two minutes. Users:
  - `run-tests.sh`, `build-player.sh` and `run-samples.sh`: the lookup, the `-createProject` start and the main Unity
    starts. `build-player.sh` drops its own 3 x 5 s retry.
  - `run-device-tests.sh`: the lookup and the Unity starts. `adbr` keeps its 5 quick tries for adb's own errors.
    A failure while the `cmd.exe` probe fails too counts as an outage: it waits with the back-off and after
    `wsl_tries` returns 5. The readiness check and install/launch then exit 5 instead of "no Android device" (2).
  - `run-emulator-tests.sh`: the lookup. A boot that fails while the probe fails returns 5 (`boot_failed`), and the
    per-level "worst" code carries a run-device-tests 5 up.

  A Unity run that never started is no longer reported as "compile errors?" or a build error.

## Files

- `Tools~/unity/wsl-interop.sh` (new); `run-tests.sh`, `run-device-tests.sh`, `run-emulator-tests.sh`,
  `build-player.sh`, `run-samples.sh` (helper adoption, exit 5 in the header comments); `skip-baseline.txt`.
- `Tools~/agent-tools/test_wsl_interop.py` (new, 17 tests). Stub `cmd.exe`, `wslpath`, `Unity.exe`, `adb.exe` and
  `rsync` follow a per-call ok/fail plan. Covered: a short outage at the lookup, at Unity start and at adb readiness
  survives; a long one exits 5 with the message and never "compile errors?"; an adb error while interop is up stays
  exit 2 after 5 tries; a stale log cannot hide a failed start; a non-path answer is exit 2; run-tests.sh end to end.
- `Tools~/agent-tools/test_emulator_windows.py`: its function harness also sources `wsl-interop.sh` (and `boot_failed`).
- `Development~/docs/agents/testing.md` (WSL paragraph, `Player-iOS` in step 1), `tooling.md` (exit codes after the
  gate table).

## Decisions

- Exit 5 for "WSL interop unavailable": unused so far. It is distinct from 2 (setup) and above 4, so
  run-emulator-tests' highest-code summary shows an outage over a result.
- Retry signals are observable facts, not stderr text. An empty `cmd.exe` answer; no Unity log after the start (the
  log is removed before the first try, so a stale one cannot hide a failed start); an adb failure while a `cmd.exe`
  probe also fails. The `UtilAcceptVsock` line goes with all of these but is not always printed.
- `build-ios-test-player.sh` is not edited: another session owns it, per the lane rules. It can source the helper,
  and its two gaps (single Unity start, 3 x 1 s lookup) stay open under #355, so this PR is "Part of #355".
- `run-samples.sh` is included: it uses the same lookup and Unity starts, so adopting the helper is cheap.

## Deferred

- #355: `build-ios-test-player.sh` adoption, owned by the iOS session (comment on #355).
- No script applies the `Player-iOS` key yet. The iOS handoff summary could pass `Player-iOS` to
  `summarize_results.py`; that belongs to the same session.

## Verification

- `python3 -m unittest discover -s 'Tools~/agent-tools' -p 'test_*.py'`: all pass, including the 17 new tests.
  Before the script edits, the interop tests for each script failed (exit 2 "cannot resolve %LOCALAPPDATA%", exit 2
  "no Android device").
- `shellcheck Tools~/unity/*.sh` clean; `./Tools~/ci/check.sh --full` (see the PR for the result at the pushed head).
- One unfiltered Android run, API 29 x86_64 emulator, Stripping High, main e0a5bf7, 2026-11-04:
  `run-emulator-tests.sh --api 29 --jobs 1 --stripping High`, exit 0, 2628 total, 2612 passed, 16 skipped. No
  qemu process was left over afterwards.
- Not run: Unity EditMode/PlayMode in the host editor (no C# changed), iOS (another session; the 21 come from its
  run 7), devices. The new scripts were not run against a real WSL interop outage: the stubs simulate it.
