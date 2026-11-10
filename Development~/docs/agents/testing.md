# Testing guide

Load when writing or running tests, or when a Unity test run misbehaves. Gate commands and CI are
in `tooling.md`.

## Layout

- `Tests/Runtime/` (`TurboHTTP.Tests.Runtime.asmdef`, references every runtime module) and
  `Tests/Editor/` (`TurboHTTP.Tests.Editor.asmdef`). Both keep `defineConstraints:
  ["UNITY_INCLUDE_TESTS"]`, `overrideReferences: true` and `precompiledReferences:
  ["nunit.framework.dll"]`.
- One folder per area: `Core`, `Pipeline`, `Transport` (`Http1`, `Http2`, `Tls`), `Proxy`, `Middleware`,
  `Retry`, `Auth`, `Cache`, `Observability`, `Files`, `JSON`, `WebSocket`, `Unity`, `Mobile`,
  `Platform`, `Performance`, `Integration`, `Compatibility`, `Extensibility`, `Testing`, `Manual`.
- Shared helpers: `Tests/Runtime/AssertAsync.cs`, `Tests/Runtime/Support/`, `MockTransport` and
  `RecordReplayTransport` (TurboHTTP.Testing), the HTTP/2 duplex-stream harness used by
  `Tests/Runtime/Transport/Http2/Http2ConnectionTests*.cs`.
- Allocation gates: `Tests/Runtime/Performance/StreamingAllocationGateTests.cs`,
  `Phase19AllocationGateTests.cs` with baselines in `Tests/Benchmarks/`. The streaming gates measure through
  `AllocationMeter` (the per-thread counter only where it counts the thread exactly, else the "GC Allocated
  In Frame" counter; IL2CPP 2021.3 rounds the per-thread counter to 4 KiB and includes other threads), judge
  the smallest of five isolated rounds against an `AllocationBudget` per profile (optimized editor,
  unoptimized Mono player, unoptimized IL2CPP player: Test Runner players compile without optimization,
  #438), and skip with `[env]` when idle frames could explain the excess.

## Writing tests

- A behavior change comes with a test that fails without it. Protocol, concurrency and
  cancellation fixes drive the hostile byte sequence or interleaving (cancel mid-frame, CONTINUATION
  flood, window decrease, EOF mid-headers) through the real code path. Do not re-implement the logic
  under test inside the test and assert on your own copy.
- Deterministic only: no wall-clock sleeps as synchronization, tolerances on scheduler-dependent
  counts (a storm test asserting ≥250 saw 249). Use `CancelAfter`, completion sources, virtual time.
- Unity 2021.3's NUnit profile:
  - no `async Task` test methods ("Method has non-void return value"): write `void` tests that wrap
    the body in `AssertAsync.Run(...)`;
  - no `Assert.ThrowsAsync`: use `AssertAsync.ThrowsAsync`; it matches the exact exception type, so
    catch base types by hand;
  - run async helpers through `Task.Run`; Unity's synchronization context does not pump in tests and
    deadlocks;
  - never call another test method from a test (it skips SetUp/TearDown).
  - no `[NonParallelizable]`, `Assert.Multiple` or `Assert.*Async`: they are
    missing from `com.unity.ext.nunit` 1.0.6 and break compilation of the whole test assembly
    (`check_repo.py unitynunit` enforces this, since PR CI cannot compile Unity tests without a
    license).
- Code must compile against Unity's .NET Standard 2.1 profile, which lacks `Encoding.Latin1`,
  `ReadOnlySequence<T>.ToArray()`, span-based `Encoding.GetBytes`, `Environment.TickCount64`,
  `ValueTask.FromException<T>` (use `new ValueTask<T>(Task.FromException<T>(…))`),
  `Assert.IsNotSame`, `out` parameters in async methods, and other .NET 5+ APIs. The shadow build
  catches most of these before CI.
- Unity Mono behaves differently from .NET 8, so a green .NET harness proves logic only:
  `GZipStream` does not throw on truncated input, `NetworkStream.ReadAsync` ignores cancellation
  tokens, duplex test streams complete synchronously, `CertificateRequest` is unavailable. Tests that
  depend on these run in the Unity jobs.
- Tests also run as iOS/Android players (the device runs under "Host-editor gates", #99), where
  `AllowCleartext` defaults to false (#85). A test that sends `http://` or `ws://` through
  `RawSocketTransport` or `RawSocketWebSocketTransport` to a loopback server opts in:
  `AllowCleartext = true` on `UHttpClientOptions` / `WebSocketConnectionOptions`, or
  `TestHelpers.AllowLoopbackCleartext(request)` for a request dispatched straight to the transport
  (#321). Without it the test passes only in the Editor.
- Player-safe test data (#321): write files under `TestTempPath.Root` or `TestTempPath.NewDirectory`
  (`Tests/Runtime/Support/TestTempPath.cs`), never `Path.GetTempPath()`, which is the unwritable
  `/data/local/tmp` in an Android player. Build NUL-containing strings at run time
  (`TestCaseSource`): IL2CPP truncates attribute strings at a NUL. A member a test reaches by
  reflection needs `[Preserve]` in its runtime assembly (a `link.xml` under `Tests/` is not read).
  Gate a test on a platform with `TestRequirements.IgnoreEnvironment` (`[env]` prefix), never by
  letting it fail.
- `AssertAsync.Run`/`ThrowsAsync` wait for the body with a bound (`AssertAsync.DefaultBound`,
  5 minutes; overloads take a shorter one) and fail with `AssertAsyncTimeoutException`, so a hung
  body fails one test instead of freezing the player run (#330). Direct
  `Task.Run(...).GetAwaiter().GetResult()` has no bound: use `AssertAsync`.
- When the transport is right and a test is wrong (e.g. an over-sending flow-control peer), fix the
  test to model a compliant peer; never relax timeouts to make a test pass.
- Tests that need the public internet carry `[Category("ExternalNetwork")]`; very slow or
  >100 MB tests carry `[Explicit]` plus `Integration` or `Performance`. No CI job runs Unity; the
  `ExternalNetwork` category runs in the host editor (`run-tests.sh --category ExternalNetwork`, see
  "Host-editor gates"), never as a gate on a PR.
- Tests are not exempt from ownership rules: dispose clients and responses (`using var`), and say so
  in a comment when a test deliberately disposes early.
- Do not encode a known bug as expected behavior; if a fix lands later, the test must change with it
  (e.g. retry tests currently assert `Retry-After` may exceed `MaxDelay`, #54).
- Renaming or removing a public type: grep `Runtime/`, `Editor/` and `Tests/` in the same change; the
  Editor monitor and tests broke on the 22.x clean break.

## Running Unity tests

- `'./Tools~/unity/run-tests.sh' --platform EditMode|PlayMode --filter <regex>` syncs the package
  into a cached temp project and runs Unity in batch mode. EditMode runs with graphics (editor-window
  tests need them).
- "Scripts have compiler errors" aborts the whole run and hides every test result. Fix compile errors
  first (typical after a rewrite: missing `using System.Threading`/`.Tasks`/`.Collections.Generic`,
  `Task`/`ValueTask` overload ambiguity, new constructor parameters), then re-run: hidden failures
  show up only after the compile fix.
- Runtime tests run in PlayMode; an EditMode run against `TurboHTTP.Tests.Runtime` discovers 0 tests.
- **UniTask adapters (#311).** `TurboHTTP.Tests.UniTask` (`UniTaskCancellationTests`) compiles only
  when the project has `com.cysharp.unitask`, which the test project does not install by default.
  Run it with `--with-unitask`, which adds UniTask from the Cysharp git repository at the tag pinned
  in `Tools~/ci/unitask_pin.py` (needs git and github.com):
  `'./Tools~/unity/run-tests.sh' --platform PlayMode --with-unitask --filter 'UniTaskCancellationTests'`.
  These are PlayMode tests. The script exits 2 when `Packages/packages-lock.json` resolved a commit
  other than the pinned SHA, and 4 when no `TurboHTTP.Tests.UniTaskAdapter` test ran (an EditMode run,
  or a filter that matches none). A run without the flag removes the package again. Unity resolves git
  packages only when git is on the editor host's PATH (it says "No 'git' executable was found"; the
  Windows host editor has none today). Without git, pass `--with-unitask=local`: it installs the same
  pinned commit from the SHA-verified checkout the shadow build fetches (git in WSL), copied next to the
  test project as a `file:` package, and the lock must then name that copy. The shadow build
  in `check.sh` compiles the same commit, so a compile error in the adapters already fails there
  (`tooling.md`, "UniTask pin", also for bumping the version).
- Read the skipped count in the result XML: a skip is not a pass (CONNECT tunnel tests skip today, #151).
- A journal or PR that says "Unity not run" or "isolated .NET harness only" is unverified. Record
  exactly which lane ran: EditMode, PlayMode, IL2CPP player, Linux player, device.

## Host-editor gates (periodic host-editor runs)

CI never runs Unity: the repository has no `UNITY_LICENSE` and will not get one (owner decision
2026-11-02, #188), so ci.yml has no `unity-tests` job and the required check `ci` covers `lint`,
`tls-interop` and `h2-interop` only. The Unity gates are these runs in a licensed local Unity 2021.3
editor (WSL + Windows editor: `UNITY_PATH=/mnt/<drive>/.../Editor/Unity.exe`), done:

- after every `turbohttp-waves` run (its Step 5), before ticking a milestone's tracking issue, and
  before closing a milestone;
- otherwise once in a while (about weekly while PRs are merging), and whenever a change touches
  test infrastructure, asmdefs or `#if` platform guards.

From a clean worktree of the commit under test (e.g. `origin/main`), one Unity process at a time:

```bash
export UNITY_PATH=<Unity 2021.3.45f2 editor binary>
'./Tools~/unity/run-tests.sh' --platform EditMode                       # 1. full suites + skip gate
'./Tools~/unity/run-tests.sh' --platform PlayMode
'./Tools~/unity/run-tests.sh' --platform PlayMode --category ExternalNetwork   # 2. internet tests
'./Tools~/unity/run-tests.sh' --platform PlayMode --with-unitask --filter 'UniTaskCancellationTests'  # 2b. UniTask adapters
'./Tools~/unity/build-player.sh' --target win64-mono                    # 3. player builds
'./Tools~/unity/build-player.sh' --target win64-mono --stripping High --network   #    Mono + Stripping High (#450)
'./Tools~/unity/build-player.sh' --target android-il2cpp                #    (+ --bouncycastle; win64-il2cpp when the SDK is installed)
'./Tools~/unity/build-player.sh' --target android-il2cpp --run [--serial <id>] [--network]  # 4. player smoke on a device/emulator
```

1. **Full suites and the skip gate.** Only this run may omit `--filter`. `summarize_results.py` exits 4
   when a skipped test has no `#issue` or `[env]` reason, or when an unfiltered run skips more tests
   than `Tools~/unity/skip-baseline.txt` allows for its run target (`EditMode 0`, `PlayMode 3`: the
   permanent host skips, #319; `Player-Android` for `run-device-tests.sh`/`run-emulator-tests.sh`, whose
   players skip more platform-scoped `[env]` tests, #341; `Player-iOS` records the iOS simulator run, which
   no script gates yet, #365; the file lists each skip with its issue or platform reason); a filtered or category run does not apply the baseline. Lower the baseline in the same PR that removes skips; never raise it to make a
   run pass, and list any justified raise in the file with its test and reason. `check.sh` enforces the static half (`ignores`: an `Ignore` needs an
   `#issue` or `[env]` reason).
2. **ExternalNetwork run (#115, #263).** Needs the public internet. `--category ExternalNetwork` implies
   `--integration`: the script defines `TURBOHTTP_INTEGRATION_TESTS` in the test project for that run
   so the internet blocks guarded by it compile (TLS integration, BouncyCastle/SslStream provider and
   TCP pool tests), and removes the define again on every other run, so scoped runs never compile
   them. A failing test is fixed, or becomes `TestRequirements.IgnoreEnvironment(...)` / an
   `Assert.Ignore("#<issue> ...")`; it is never left red. The two loopback tests behind the same define
   (`Parse_ReadToEnd_ExceedsMaxBodySize_ThrowsIOException`, a 100 MB body, and
   `RawSocketTransport_FreshConnection_IOException_NoRetry`) are not `ExternalNetwork`: run them with
   `--platform PlayMode --integration --filter 'ExceedsMaxBodySize|FreshConnection_IOException_NoRetry'`.
3. **Player builds (#273, #100).** `build-player.sh` builds Mono Win64, Android IL2CPP with and without
   `--bouncycastle`, and IL2CPP Win64 when the Visual Studio C++ toolchain is present, runs the Windows
   players and checks the linker output (BouncyCastle assemblies absent without the opt-in). The smoke
   player logs one `TURBOHTTP_SMOKE ROW <id> PASS|FAIL|SKIP <issue> <detail>` line per row (transport
   registration #1, link.xml reflection targets #8, WebSocket JSON DTO with a nested list and dictionary
   #192, OAuth refresh #67, `GetJsonCoroutine<T>` for a class and a struct #95, offline through an
   in-process transport; with `--network` one HTTPS request per TLS provider, #141/#258) and one aggregate
   `TURBOHTTP_SMOKE PASS|FAIL` line the script requires. Mono at `--stripping High --network` is a
   required release-candidate row (#450): the offline `net-config` row runs `ServicePointManager`'s
   configuration init and `tls-sslstream` must complete a handshake with a 200, both of which fail when
   UnityLinker strips what that init creates by reflection (`Runtime/Transport/link.xml` keeps it).
   `--stripping Low|Medium` take the same path when a stripping change needs them. iOS and device
   rows stay `needs-device-validation` (#99).
4. **Android device run (#99).** With one Android device on `adb` (unlocked, screen kept on:
   Developer options → Stay awake), `'./Tools~/unity/run-device-tests.sh' [--stripping Low|High]
   [--arch arm64|armv7|x86_64] [--filter ...] [--category ...]` builds the PlayMode suite as an IL2CPP player,
   installs it and runs it on the device. Results come back over the player connection when it works;
   otherwise (LAN or firewall: the Editor needs an inbound rule for the Private profile) the script waits
   for the XML the player writes on the device and pulls it over adb. That wait ends when the result file
   appears or the player's process is gone (three complete answers without it, 20 s apart: about a minute;
   an empty, failed or truncated `pidof`/`ps` answer counts as unknown, never as gone, #412), and is capped by
   `TURBOHTTP_DEVICE_TEST_WAIT_MIN` (default 120). A player that wrote its results but did not quit is then
   force-stopped. The device log of every run is kept as `logcat-Android-<tag>-<ts>.txt` in the cache. A
   run refuses (exit 2) to start while the player of an earlier run is still running its tests on the device
   (no result file yet), so a queued run never kills it; with `--serial`, a launch that starts no process (a
   lock screen, or a parental-control screen holding the focus) exits 2 naming the focused window, and a
   player whose process runs but never starts its tests (left behind the launcher) is relaunched once, then
   exits 2. Both verdicts need complete adb answers; an unanswered check hands over to the wait.
   The device-only test project (`UNITY_DEVICE_TEST_PROJECT`, never `run-tests.sh`'s `UNITY_TEST_PROJECT`)
   opts into cleartext and points `TMPDIR` at the app cache; neither touches the package. The player
   smoke runs next to it: `'./Tools~/unity/build-player.sh' --target android-il2cpp [--bouncycastle] --run
   [--serial <id>] [--arch arm64|x86_64] [--network]` checks the device on adb first (`--arch` defaults
   to its ABI, so an emulator serial gets an x86_64 APK), builds, installs (`adb install -r`),
   force-stops, wakes the screen (exit 2 on a lock screen it cannot dismiss), launches
   `UnityPlayerActivity` and polls logcat for the aggregate marker (`TURBOHTTP_SMOKE_WAIT_S`, default
   300 s); exit 1 on FAIL or no marker, logcat kept as `logcat.txt` in the build folder. `--apk <file>`
   reruns a built APK without Unity; `--network` (opt-in, needs the internet) adds the TLS rows and
   requires their PASS. Quote the row lines in the #99 journal. Never
   stop a running device run from the shell: killing Unity's process tree also kills its adb server.
   With the phone and an emulator (or several) attached, pass `--serial <id>`: Unity only builds the APK
   and the script installs and launches it on that device; `--skip-build` reinstalls the last such APK.
   `'./Tools~/unity/run-emulator-tests.sh' [--api 23,26,29,33,36] [--jobs 2]` boots one x86_64 AVD per API
   level (`turbohttp-api<N>`, wiped each boot), builds the x86_64 player once and runs the suite on each.
   It needs an SDK with `emulator` and the `default;x86_64` system images (`TURBOHTTP_EMULATOR_SDK`,
   default `<cache>/android-sdk`: `${XDG_CACHE_HOME:-~/.cache}/turbohttp`, or `%LOCALAPPDATA%\turbohttp`
   from WSL; filled with the editor's `cmdline-tools` 6.0 sdkmanager) and WHPX/KVM. Ctrl-C stops every
   lane (its run-device-tests.sh and Unity) and then the emulators. From WSL the emulators are Windows
   processes the shell cannot see: after each level and on exit the script finds `emulator.exe`/`qemu-system-*`
   by `-avd turbohttp-api<N>` (PowerShell CIM query) and stops what the console left with `taskkill /T /F`
   (`emulator-windows.sh`, #353). Before a boot it stops a `turbohttp-api*` leftover of an earlier run on that
   AVD or port, and fails the level (exit 2) naming the PID of any other emulator on its port. Emulator results cover API-level
   behavior only: x86_64, no radio, no vendor stack, so ARM/IL2CPP and network rows still need the phone.
5. **iOS simulator run (#99).** `'./Tools~/unity/build-ios-test-player.sh' [--out <dir>] [--stripping
   Low|High] [--filter ...]` builds the PlayMode suite as an IL2CPP Simulator SDK player and exports the
   Xcode project (a Windows editor cannot run it); a Mac builds it with `xcodebuild`, runs it on a
   simulator and reads `results.xml` from the app's data container. The handoff folder is local: pass
   `--out <handoff>/xcode` or export `UNITY_IOS_HANDOFF_OUT=<handoff>/xcode` (the `xcode/` folder, not the
   handoff folder itself; any other last component exits 2), and keep machine paths out of the repo,
   issues and PRs. The script removes `READY`, `DONE`
   and `out/` next to `--out` before the build and creates `READY` after the copy. The player logs
   `TURBOHTTP_DEVICE_TEST`/`_FAIL`/`_HANG` markers, rewrites `results.xml` after every fixture
   (`partial="true"`) so a hang still leaves results, and quits when the run ends. Unity 2021.3 ships an
   x86_64-only simulator player, which Apple Silicon runtimes (iOS 26+) cannot run; use a Unity 6 editor
   with iOS Build Support there (the script sets the ARM64 simulator architecture) and record the editor
   version with the results, since it is not the 2021.3 baseline. A simulator run does not replace the
   device rows (ATS, NAT64, ALPN, suspension stay `needs-device-validation`).
6. **Certification matrix (Phase 15.6, #105).** `UnityReliabilitySuiteTests` (dispatcher flood,
   cancel-before-drain, the domain-reload transition), `UnityStressSuiteTests` (texture and audio
   bursts) and `UnityPerformanceBudgetTests` are the certification suites. Their budgets live in
   `Tests/Runtime/Unity/ReliabilityBudgets.cs`, per profile (`editor`, `standalone`, `mobile`; the mobile
   values are estimates until device runs, #99). Frame time is the dispatcher's measured per-frame
   drain (`MainThreadDispatcher.GetDrainStats`), never a sleep. Each budget prints one line in the
   test's NUnit output: `TURBOHTTP_METRIC name=<id> value=<n> budget=<n> unit=<u> profile=<p> result=pass|fail`.
   After the runs, collect the result XMLs from the runners' cache folder into the matrix:
   `python3 'Tools~/unity/platform_matrix.py' <cache>/results-PlayMode-<ts>.xml <cache>/results-Android-High-x86_64-api33-<serial>-<ts>.xml`.
   It writes `TestResults~/unity-platform-matrix.json` (gitignored; `--out` elsewhere) and prints a
   summary for the PR or journal. The runners' file names give each row its target, backend,
   stripping, arch and API level; anything else takes `--set key=value` (an iOS simulator
   `results.xml`: `--set target=iOS --set backend=IL2CPP --set device=simulator --set unity_version=<editor>`).
   Rows already in the file stay unless a new row has the same id (`--fresh` starts over); `--gate`
   exits 1 when a cell is `fail` or `incomplete`. The cells are fixed and their ids stable (schema
   `turbohttp.unity-platform-matrix` version 1; fields are only added): `editor-editmode-mono`,
   `editor-playmode-mono`, `standalone-*`, `android-il2cpp-arm64`, `android-il2cpp-x86_64-emulator`,
   `ios-il2cpp` and `webgl`. A cell no row reached reads `not-run`, `needs-device-validation` (arm64
   phone, iOS, macOS) or `policy-disabled` (WebGL: no socket transport). A cell that ran lists the
   certification suites it missed; a certifying cell (every cell but EditMode) whose rows passed
   without all of them reads `incomplete`, not `pass` (a `--filter` run certifies nothing). Its unit
   tests (`Tools~/unity/test_platform_matrix.py`) run in `check.sh --full`. CI does not build the matrix: it has no Unity license (#188).

7. **Linux standalone player (Mono, #423).** Unity 2021.3's Mono runtime sometimes dies with SIGSEGV in the
   Boehm GC (`GC_mark_from`) while the Test Runner builds the test tree, before any test runs. The log has
   `Caught fatal signal` and no `TURBOHTTP_DEVICE_RUN started`. On WSL2 this happened in 10 of 140 launches
   with incremental GC on and 0 of 140 with it off, and also with every TurboHTTP startup hook removed, so it
   is a runtime issue, not package code. Run the player (built by the Test Runner, `-runTests -testPlatform
   StandaloneLinux64`, on the project `build-ios-test-player.sh` writes) through
   `'./Tools~/unity/run-linux-player-tests.sh' [--out <dir>] [--attempts <n>] <player folder | .x86_64 binary>`.
   It relaunches a launch that crashed before `started`, up to `--attempts` launches in all (default 3, env
   `TURBOHTTP_LINUX_PLAYER_ATTEMPTS`; `TURBOHTTP_LINUX_PLAYER_TIMEOUT` bounds each launch, default 3600 s).
   A crash, timeout or exit after `started` is a real result and is never relaunched. Exit codes as
   `run-tests.sh`: 0 pass, 1 failures, 2 setup error (no player; every launch crashed before the run; a pre-run timeout or
   non-crash exit; a crash, timeout or exit after `started` without a finished line, partial results at the
   player's persistentDataPath/results.xml; no results file; the runner's stderr line names the case).
   `--out` receives `player-<k>.log`, `player-<k>.out` per launch and `results.xml`. The runner leaves the GC
   mode as built; turning incremental GC off for a crash-free row is a build choice (#404). It does not use
   `wsl-interop.sh`. Whether a native non-WSL2 Linux host also crashes is untested (follow-up #436).

From WSL the scripts detect `Unity.exe`, keep their projects under `%LOCALAPPDATA%\turbohttp` and pass
Windows paths (Unity cannot open a project on `\\wsl$`); set `UNITY_TEST_PROJECT` and
`UNITY_PACKAGE_COPY` per lane so two runs never share a project. The Editor must be licensed on that
machine (a Unity Personal license activated through the Hub is enough). Agents working on one change
never run the whole suite.

WSL-to-Windows interop fails now and then, sometimes for a minute or more (`cmd.exe`, `adb.exe` and
`Unity.exe` do not start; stderr shows `UtilAcceptVsock ... accept4 failed 110`). `run-tests.sh`,
`run-device-tests.sh`, `run-emulator-tests.sh`, `build-player.sh`, `run-samples.sh` and
`build-ios-test-player.sh` source `Tools~/unity/wsl-interop.sh` (#355): the `%LOCALAPPDATA%` lookup, a Unity start that wrote no log and an
`adb.exe` call that failed while `cmd.exe` does not answer either are retried with a growing pause (about
two minutes). A longer outage prints `WSL interop unavailable` and exits **5**: not a test, compile or
device result, so rerun once `cmd.exe /c ver` answers.

Afterwards:

- every failure, compile error or newly Ignored test becomes a fix or a `type:bug` issue in the
  owning milestone (search for an existing one first);
- record the run in a journal file (`implementation-journal/YYYY-MM-unity-host-run.md` is the running
  log: append a dated section per run, or add a new dated file when your lane may not edit it):
  commit, totals per platform (passed/failed/skipped/inconclusive) and the issues filed. A milestone
  closes only after a green run on its final commit.

## Reviews of test changes

- A phase is not done until every test file its plan lists exists; check the plan's file list.
- Re-run both specialist rubrics after each fix round; fixes have repeatedly introduced sibling bugs.
- Verify reviewer claims against the code before acting on them; earlier reviews contained false
  positives.
