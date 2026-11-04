# 2026-11: Unity tool hygiene (#353, #352)

## What

- **#353, leftover Windows emulators.** On WSL2 with a Windows editor, `run-emulator-tests.sh` starts
  `emulator.exe` in the background, so `$!` is the WSL interop pid. The Windows `emulator.exe` and its
  `qemu-system-x86_64-headless.exe` child can outlive that pid. `stop_emulator` saw `kill -0` fail and
  returned "stopped", `adb emu kill` had nothing to talk to once adb dropped the serial, and the leftover kept
  `turbohttp-api<N>` locked ("Running multiple emulators with the same AVD" at the next boot). The new
  sourced helper `Tools~/unity/emulator-windows.sh` lists Windows `emulator*`/`qemu-system*` processes with a
  PowerShell CIM query (`pid|CommandLine`, ended by a marker line, retried 3 times because WSL interop calls
  fail now and then) and parses `-avd`/`-port` from each command line.
  - `win_stop_avd <avd> <polls>`: waits for the AVD's processes to exit, then runs `taskkill /PID <pid> /T /F`
    and checks again. If a process is still there, or if Windows cannot be asked, it prints a message naming
    the AVD (and PIDs). A failed lookup never counts as "no process".
  - `win_free_avd <avd> <port>`: runs before each boot. It stops `turbohttp-api<N>` leftovers that run this
    AVD or hold the console port. If any other emulator holds the port, it fails naming the PID and does
    not stop it. The lane then records exit 2.
  - `run-emulator-tests.sh` (Windows path only) calls `win_free_avd` in `boot`, `win_stop_avd <avd> 15` in
    `stop_emulator` (the same 30 s budget as before), and `win_stop_avd <avd> 5` for every started AVD in the
    EXIT trap. The trap needs the AVD names because lanes are subshells and their `emu_pid` is not visible
    there (`started_avds` beside `started`). The Linux/KVM path did not change.
- **#352, machine paths.** The usage headers of `build-player.sh`, `run-samples.sh`, `run-tests.sh`,
  `run-device-tests.sh` and `run-emulator-tests.sh` now use `<Unity 2021.3 editor binary>`
  (`run-tests.sh`'s WSL example uses `/mnt/<drive>/...`). `how-to-run-tests-with-temp-unity-project.md` now
  uses `<repo>`, `<test-project>` and `<unity>`. The phase 3C testing addendum uses relative links, and its
  reference to a local agent-review note is plain text. `run-allocation-gate.sh` requires `PROJECT_PATH`
  and exits 2 with a usage line when it is missing. `test_unitask_pin.py`'s synthetic Windows path changed
  from `C:/Users/u/...` to `C:/home/u/...`.

## Decisions

- **The "foreign holder" case is identified by console port.** Every process that runs `-avd turbohttp-api<N>`
  is one of this script's AVDs by name, so the "else fail naming the PID" branch is for another emulator
  (Android Studio's, say) on the console port this level needs. That emulator would also take the adb serial
  `emulator-<port>` that the script drives. The script stops only command lines whose AVD matches
  `^turbohttp-api[0-9]+$`.
- **A refused level sends no console kill (review round 1).** When the pre-boot check refuses a foreign
  emulator, the level's serial `emulator-<port>` is that emulator's console. `boot` writes
  `<out>/emulator-<port>.launched` only after the check passes; `stop_emulator` does nothing when the lane
  launched no emulator, and the EXIT trap sends `emu kill` only to serials with that marker.
- **A `turbohttp-api*` process is assumed to be a leftover.** Runs are serialized by `flock` on the host
  editor, so a running `turbohttp-api*` emulator that a level's boot needs is a leftover of an earlier run.
- **Root cause.** The intermittent leak itself (the console sometimes does not answer before the serial
  leaves adb) has no known root cause in the interop launcher. The fix is the AVD-name lookup, not a fix
  for the launcher.
- **#352 rg check.** The check uses `--glob '!**/implementation-journal/**' --glob '!**/reviews/**'`, because
  the issue's `!implementation-journal/**` is anchored to the working directory and does not exclude
  `Development~/docs/implementation-journal`. The journal is history and stays as it is. The
  `test_unitask_pin.py` literal was reworded rather than excluded.
- **`UNITY_BIN`.** `run-allocation-gate.sh` keeps its `UNITY_BIN` default, the standard macOS Hub install path
  (not machine-specific, and the check does not match it). Only `PROJECT_PATH` is required. The `/Applications/...` install
  instructions in `tooling.md` stay for the same reason.

## Files

`Tools~/unity/emulator-windows.sh` (new), `Tools~/unity/run-emulator-tests.sh`, the four other
`Tools~/unity/*.sh` headers, `Development~/scripts/phase19/run-allocation-gate.sh`,
`Development~/docs/how-to-run-tests-with-temp-unity-project.md`,
`Development~/docs/phases/phase3c/step-3c.11-testing-addendum.md`, `Development~/docs/agents/testing.md` (step 4),
`Development~/docs/agents/tooling.md` (emulator row), `Tools~/ci/test_unitask_pin.py`,
`Tools~/agent-tools/test_emulator_windows.py` (new).

## Verification

- `Tools~/agent-tools/test_emulator_windows.py` (auto-discovered by `check.sh --full`) has 12 tests. Stub
  `powershell.exe`/`taskkill.exe` on PATH edit a fake process table and cover these cases:
  - the stop path kills only `-avd turbohttp-api33` (it ignores `turbohttp-api3` and a foreign AVD)
  - an emulator that already exited needs no taskkill
  - a survivor is reported
  - the lookup retries a failed interop call
  - a failed lookup is not read as "no process"
  - a pre-boot leftover (same AVD, or another `turbohttp-api*` on the port) is stopped
  - a foreign emulator on the port fails with its PID
  - with a foreign emulator on the port, the script's own `lane` and `cleanup` functions (adb and the
    emulator stubbed) send no `emu kill`, launch nothing and record exit 2; the trap kills only marked serials
  - the run script calls the helpers on the Windows path and in the trap
  - the allocation gate exits 2 with usage when `PROJECT_PATH` is unset
  - no `/mnt/<x>/` or `/Users/` path is left outside the journal and reviews

  All 12 failed with the old scripts restored and pass with the change.
- `shellcheck` is clean on the changed scripts.
- Real API 29 runs on the host editor (`run-emulator-tests.sh --api 29 --jobs 1 --stripping High --filter
  'HttpMethodTests|BackoffJitterTests'`):
  1. The first run, with three quick lookup tries, booted the emulator. Then every WSL interop call
     (adb.exe, powershell.exe) failed with `UtilAcceptVsock ... accept4 failed 110` for about a minute.
     The device run found no device, the stop lookup gave up ("cannot list Windows processes"), and the
     qemu of `turbohttp-api29` stayed. After that run, lookups back off for about two minutes before they
     give up.
  2. The second run stopped at the existing `%LOCALAPPDATA%` lookup during the same kind of outage.
  3. The third run found the leftover from run 1, stopped it before the boot ("a previous run left
     turbohttp-api29 running; stopping it", taskkill on its PID), and passed 5/5 tests (exit 0).
     Afterwards only the emulator's own `emulator -kill <pid> -sleep 20` helper was listed, and it
     exited by itself 20 s later. `Get-Process qemu*,emulator*` then printed nothing.
- Interop outages also break adb and `cmd.exe` calls elsewhere in the scripts. Hardening those is not part of
  this change.
- Not run: Unity EditMode/PlayMode (no runtime code changed), IL2CPP beyond the emulator run's player
  build, the phone, a Linux/KVM host, and the Ctrl-C and frozen-emulator paths against a real emulator (they
  are covered only by the stubs).
