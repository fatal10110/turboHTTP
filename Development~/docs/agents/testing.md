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
- Unity Mono behaves differently from .NET 8, so a green .NET harness (the `unit` job below) proves
  logic only: `GZipStream` does not throw on truncated input, `NetworkStream.ReadAsync` ignores
  cancellation tokens, duplex test streams complete synchronously, `CertificateRequest` is unavailable,
  and continuations Mono runs inline may still be queued on .NET. Tests that depend on these run in the
  host-editor Unity runs and are excluded from `unit` by name.
- A Unity-free test (no `UnityEngine`, `UnityEditor` or `[UnityTest]` in its file) runs on every PR in the
  `unit` job, so it must also pass on .NET 8. Keep engine calls out of shared helpers, or the helpers'
  users drop out of `unit` (see "Unity-free unit job").
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
  >100 MB tests carry `[Explicit]` plus `Integration` or `Performance`. No CI job runs Unity (`unit` runs
  the Unity-free tests on .NET 8 and skips `ExternalNetwork`); the `ExternalNetwork` category runs in the host editor (`run-tests.sh --category ExternalNetwork`, see
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
  Any run in which no test ran (that, or a `--filter` that matches nothing) exits 4 (#903), so a scoped
  run that passes ran at least one test; still check that the total matches what the filter should select.
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

## Unity-free unit job (`unit`, #879)

The required check `ci` needs the `unit` job, which runs the engine-free part of `Tests/Runtime` on .NET 8
(`Tools~/unit-tests/`). It is a logic gate on every PR, not a replacement for the Unity runs.

- **Selection is automatic.** `TurboHTTP.UnitTests.csproj` (target `SelectUnityFreeTests`) compiles every
  `Tests/Runtime/**/*.cs` whose text mentions none of `UnityEngine`, `UnityEditor`, `UnityTest`; a new
  Unity-free file is picked up without editing anything. The build prints the counts (`unit: N files
  selected, ...`).
- **Exclusions are explicit and commented.** Whole files: `UnitExclude` in the csproj (today
  `Tests/Runtime/Unity/**`, whose tests create engine objects). Single tests: `excluded-tests.txt`, one fully
  qualified name per line with the reason above it. A test that fails only on .NET is fixed when it is a
  test or product defect; otherwise it is excluded with a follow-up issue named in the comment.
  `UnitHelper` force-includes the few shared helpers that mention `UnityEngine` but whose engine call the
  harness never reaches (`TestTempPath`, `BoundedShutdown`).
- **Environment.** The editor shadow variant's DLLs and defines (`UNITY_EDITOR`, `UNITY_INCLUDE_TESTS`,
  the opt-in `TURBOHTTP_BOUNCYCASTLE`/`TURBOHTTP_BACKGROUND_NETWORKING`), NuGet NUnit 3.14 (the suite is
  written to Unity's NUnit 3.5 subset, which `check_repo.py unitynunit` enforces), assembly name
  `TurboHTTP.Tests.Runtime` (the modules' `InternalsVisibleTo`), and ICU globalization (the IDN host tests
  need it). `Tools~/unit-tests/UnitHarnessStartup.cs` runs the registrations the Editor runs at domain load
  (default transports, the JSON bridge, the BouncyCastle provider); `JsonUtilitySerializer` stays
  unregistered (native engine code).
- **Run it locally:** `'Tools~/unit-tests/run.sh'` (add `--no-shadow-build` to reuse `.build/shadow`,
  `--filter 'FullyQualifiedName~CookieJar'` to narrow). Exit 0 pass, 1 failure, 2 usage, 3 dotnet missing;
  TRX results in `.build/unit/results`. About 2 minutes for the tests after the build on a 4-core box.
- **Not covered here:** `[UnityTest]` coroutines, engine objects, Mono- or IL2CPP-specific behavior, the
  `ExternalNetwork` category and the `Tests/Editor` assembly. The asmdef and `link.xml` text invariants in
  `Tests/Editor` (`ModuleAutoReferenceTests`, `TurboHttpLinkerProcessorTests`) stay host-editor only; `check_repo.py`
  enforces the module dependency rules on every PR independently.

## Host-editor gates (periodic host-editor runs)

CI never runs Unity: the repository has no `UNITY_LICENSE` and will not get one (owner decision
2026-11-02, #188), so ci.yml has no `unity-tests` job and the required check `ci` covers `lint`,
`tls-interop`, `h2-interop` and `unit` (the Unity-free tests on .NET 8, above). The Unity gates are these runs in a licensed local Unity 2021.3
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
'./Tools~/unity/build-player.sh' --target android-il2cpp --run [--serial <id>] [--network] [--suspend]  # 4. player smoke on a device/emulator
```

A release candidate adds the certification matrix (item 8): the Win64 standalone test players
(`run-tests.sh --platform StandaloneWindows64 --backend Mono|IL2CPP`), the device and emulator runs, and
`platform_matrix.py --fresh ... --gate` over all of their result files.

1. **Full suites and the skip gate.** Only this run may omit `--filter`. `summarize_results.py` exits 4
   when a skipped test has no `#issue` or `[env]` reason, or when an unfiltered run skips more tests
   than `Tools~/unity/skip-baseline.txt` allows for its run target (`EditMode 0`, `PlayMode 3`: the
   permanent host skips, #319; `Player-Android` for `run-device-tests.sh`/`run-emulator-tests.sh`, whose
   players skip more platform-scoped `[env]` tests, #341; `Player-iOS` records the iOS simulator run, which
   no script gates yet, #365; the file lists each skip with its issue or platform reason); a filtered or category run does not apply the baseline.
   The same file keeps a `min-total <run target> <n>` line per scripted target (#903): an unfiltered run
   that ran fewer cases exits 4 and prints the shortfall, because a fixture that is deleted or compiled out
   on a target lowers the total and the skips together. Raise a minimum when a periodic run reports more
   tests; lower it only in the PR that removes tests, naming them. Any run with no test at all exits 4. Lower the baseline in the same PR that removes skips; never raise it to make a
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
   in-process transport; with `--network` one HTTPS request per TLS provider, #141/#258, and the four
   HTTP/2-over-TLS rows per provider, #1210: 2 MiB down, 1 MiB echoed up, 100 concurrent streams, a
   cancelled streaming read then 2 MiB down, against `Tools~/h2-interop/run.sh --serve`, which the script
   starts unless `TURBOHTTP_H2_ROWS_URL`/`_ROOT` name a running server; BouncyCastle must PASS over h2,
   SslStream is SKIP with a test root; the `ws-suspend-resume` row, #1132, runs only with `--suspend`, item 4)
   and one aggregate `TURBOHTTP_SMOKE PASS|FAIL` line the script requires. Mono at `--stripping High --network` is a
   required release-candidate row (#450): the offline `net-config` row runs `ServicePointManager`'s
   configuration init and `tls-sslstream` must complete a handshake with a 200, both of which fail when
   UnityLinker strips what that init creates by reflection (`Runtime/Transport/link.xml` keeps it).
   `--stripping Low|Medium` take the same path when a stripping change needs them. iOS and device
   rows stay `needs-device-validation` (#99).
4. **Android device run (#99).** With one Android device on `adb` (unlocked, screen kept on:
   Developer options → Stay awake), `'./Tools~/unity/run-device-tests.sh' [--stripping Low|High]
   [--arch arm64|armv7|x86_64] [--serial <adb serial>] [--filter ...] [--category ...]` builds the PlayMode suite
   as an IL2CPP player, installs it and runs it on the device. A full-suite run (a release candidate, #404, or a
   #99 sign-off) passes `--serial`, so the script launches the player itself and keeps the device log of the
   whole run (below). Results come back over the player connection when it works;
   otherwise (LAN or firewall: the Editor needs an inbound rule for the Private profile) the script waits
   for the XML the player writes on the device and pulls it over adb. That wait ends when the result file
   appears or the player's process is gone (three complete answers without it, 20 s apart: about a minute;
   an empty, failed or truncated `pidof`/`ps` answer counts as unknown, never as gone, #412), and is capped by
   `TURBOHTTP_DEVICE_TEST_WAIT_MIN` (default 120). A player that wrote its results but did not quit is then
   force-stopped. The device log of every run is kept as `logcat-Android-<tag>-<ts>.txt` in the cache. It is
   polled through the wait for the device-side result file (`logcat -t <epoch time>` each check, #419). With
   `--serial` (and in every `run-emulator-tests.sh` level) that wait starts at the launch, so the log covers the
   run from the launch to the end even when the run logs more than the 16M ring buffer holds; each poll is a
   bounded foreground adb call, so no log process outlives the script. Without `--serial`, Unity deploys the
   player and holds the run: polling starts only after Unity returns, so the log keeps only what the ring
   buffer still holds then, or nothing when the results came over the player connection (#1220). A logcat without `-v epoch` (before Android 7) falls back to
   one dump at the end, which keeps only the last 16M. A
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
   300 s, 900 s with `--network`); exit 1 on FAIL or no marker, logcat kept as `logcat.txt` in the build folder. `--apk <file>`
   reruns a built APK without Unity; `--network` (opt-in, needs the internet) adds the TLS and HTTP/2 rows
   and requires their PASS (a loopback row server is reached through `adb reverse`). `--suspend` (#1132) adds
   the `ws-suspend-resume` row (SKIP without it): a WebSocket with a 1 s ping interval and a 2 s pong timeout to
   a loopback echo server inside the player, which answers each ping after 1 s. When the row logs
   `TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready` (a ping in flight), the script presses Home
   (`input keyevent KEYCODE_HOME`), freezes the process, waits `TURBOHTTP_SMOKE_SUSPEND_S` (default 10 s, at
   least 5 s, else exit 2), thaws it and brings the app back with a launcher intent. Home alone is not a
   suspension: an API 33 emulator kept the backgrounded smoke running (`max-ping-gap=2.0s` over 46 s, #1281).
   The freeze method is chosen before the launch and printed (`freeze method ...`): `root` (`kill -STOP`, 1 s
   after Home so Unity sees the pause, then `kill -CONT` before the launcher intent, from a root shell: adbd
   already root or `adb root` allowed, as on emulator `google_apis` images but not `google_apis_playstore` or
   production phones), else `run-as` (the same signals as the app's uid; only a debuggable APK, which the smoke
   is not), else `freezer` (the cached-apps freezer is on, API 31+: the system freezes the cached app after its
   debounce and thaws it on the resume, so raise `TURBOHTTP_SMOKE_SUSPEND_S` past that debounce). When none works
   the script exits 2 naming each refusal. After the run it prints
   `--suspend: freeze=<method> frozen=<s>s; background=... max-ping-gap=... grace-waits=... resume-signal=...`.
   The row must PASS: the connection is still Open about 8 s after `OnApplicationPause(false)` and an echo round
   trip works (FAIL on any close, PongTimeout being the #1132 regression), and its `max-ping-gap` must be at
   least half of `TURBOHTTP_SMOKE_SUSPEND_S`, else the script FAILs the run (exit 1, "the process was not
   frozen"): a process that kept running is no evidence for #1132. Its line reports `background=`,
   `ping-in-flight-at-pause=`, `max-ping-gap=` (about the freeze when the process was frozen; about 2 s when it
   kept running), `grace-waits=`/`liveness-extensions=` (the library's keep-alive counters, read by
   reflection: the grace path) and `resume-signal=` (the resume reached the keep-alive). The server is in the same process, so
   the row exercises the ordering of timers and read completions after a process-wide freeze, not network
   delay; whether a ping is in flight at the freeze is not controlled (about half the time). Quote the row lines in the #99 journal. Never
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

   **iOS suspend/resume row (#1132), by hand.** `build-player.sh` has no iOS target and the test player
   does not background itself, so the `ws-suspend-resume` row (item 4) runs from the player smoke built in
   the Mac's editor (2021.3 with iOS Build Support; Unity 6 on Apple Silicon, as above):
   1. Copy `Tools~/unity/player-smoke/` into `Assets/TurboHttpSmoke/` of a throwaway project that
      consumes this package (the `testproj/` a `build-player.sh` run leaves in its cache has it already;
      never the iOS test project, which this smoke would quit). Switch to iOS, set the bundle identifier
      `com.turbohttp.playersmoke`, Managed Stripping Level High and Target SDK Simulator SDK, build, and
      build the exported Xcode project for a booted simulator (`xcodebuild ... -sdk iphonesimulator`).
   2. `xcrun simctl install booted <app>`, then start it with the option and its console:
      `SIMCTL_CHILD_TURBOHTTP_SMOKE_SUSPEND=1 xcrun simctl launch --console-pty booted com.turbohttp.playersmoke`
      (the environment variable reaches the player as `TURBOHTTP_SMOKE_SUSPEND=1`; the
      `-turbohttpSmokeSuspend` argument after the bundle id works too where the player sees launch
      arguments). The `ws-suspend-resume` line reads `SKIP ... suspend off` when neither reached it.
   3. When the console prints `TURBOHTTP_SMOKE SUSPEND ws-suspend-resume ready`, go to the Home screen
      (Simulator: Device → Home, Shift-Command-H), wait at least 10 s (longer than the 2 s pong
      timeout; iOS suspends a backgrounded app within seconds), then bring the app back by tapping its
      icon (or the App Switcher). Do it within 180 s of the ready line, or the row FAILs as never resumed.
   4. About 10 s later the console prints `TURBOHTTP_SMOKE ROW ws-suspend-resume PASS|FAIL #1132 ...` and the
      aggregate line. PASS needs the connection still Open and an echo round trip; record the whole row
      line (`background=`, `ping-in-flight-at-pause=`, `max-ping-gap=`: about the background time when the
      app was suspended, `grace-waits=`, `resume-signal=`) with the simulator runtime and editor version.
      A `max-ping-gap` of about 2 s means the app kept running in the background (#1281): no evidence
      for #1132, whatever the row says.
      Repeat a few times for a run with `ping-in-flight-at-pause=yes`.

   An iPhone follows the same steps: install from Xcode, set `TURBOHTTP_SMOKE_SUSPEND=1` in the scheme's
   environment (or launch with `xcrun devicectl device process launch` and that variable), read the lines in
   Console.app filtered on `TURBOHTTP_SMOKE`, and background the app from its Home screen. A debugger
   attached by Xcode can keep the app from being suspended, so start it without one (or detach before
   pressing Home). Simulator and device runs are recorded as `needs-device-validation` evidence on #1132
   until a device run is in the journal; the steps are confirmed on the first run.
6. **Certification matrix (Phase 15.6, #105).** `UnityReliabilitySuiteTests` (dispatcher flood,
   cancel-before-drain, the domain-reload transition), `UnityStressSuiteTests` (texture and audio
   bursts) and `UnityPerformanceBudgetTests` are the certification suites. Their budgets live in
   `Tests/Runtime/Unity/ReliabilityBudgets.cs`, per profile (`editor`, `standalone`, `mobile`; the mobile
   values are estimates until device runs, #99). Frame time is the dispatcher's measured per-frame
   drain (`MainThreadDispatcher.GetDrainStats`), never a sleep. Each budget prints one line in the
   test's NUnit output: `TURBOHTTP_METRIC name=<id> value=<n> budget=<n> unit=<u> profile=<p> result=pass|fail`.
   After the runs, collect the result XMLs from the runners' cache folder into the matrix:
   `python3 'Tools~/unity/platform_matrix.py' <cache>/results-PlayMode-<ts>.xml <cache>/results-Android-High-x86_64-api33-<serial>-<ts>.xml`.
   `run-tests.sh --platform StandaloneWindows64 --backend Mono|IL2CPP` (#404) writes
   `results-StandaloneWindows64-<backend>-<ts>.xml`, which fills `standalone-windows`; a Linux player's
   `results.xml` (item 7) takes `--set target=StandaloneLinux64 --set backend=Mono`.
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

8. **Release-candidate matrix, tiering and capacity (#404, Phase 15.6 Step 3).** The Win64 standalone
   suite runs through the Unity Test Runner: `'./Tools~/unity/run-tests.sh' --platform StandaloneWindows64
   --backend Mono|IL2CPP` builds the test player in its own project (`UNITY_STANDALONE_TEST_PROJECT`,
   default `<cache>/standalone-testproj`), starts it on the editor host and reads the results over the
   player connection. Mono builds at Unity's default stripping (Disabled), IL2CPP at High and needs the
   Windows IL2CPP module and the Visual Studio C++ toolchain. The player opens a window, so the host needs
   a desktop session; it is not headless. Its gates use the `Player-StandaloneWindows64` entries of
   `skip-baseline.txt`, recorded from a complete Mono run (#1219); the IL2CPP backend's first complete run, which
   needs the Windows 10 SDK and MSVC (#1225), checks them. Tiers:
   - **Per PR:** the scoped `--filter` run of the tests the change touches (CLAUDE.md section 5), never the
     whole suite. CI runs no Unity (#188).
   - **Periodic host-editor run** (the list above): full EditMode and PlayMode suites with the skip and
     total gates.
   - **Per release candidate:** the full matrix on the RC commit: the host-editor run, both Win64
     standalone backends, the Linux player when a Linux host is at hand, the Android emulator levels
     (`run-emulator-tests.sh`) and the arm64 phone, then
     `python3 'Tools~/unity/platform_matrix.py' --fresh --out TestResults~/unity-platform-matrix.json --gate <every result file>`.
     The JSON is attached to the GitHub Release (`release-checklist.md`). A red (`fail`) or `incomplete`
     cell fails the gate; `needs-device-validation` (macOS, iOS without a Mac run) and `policy-disabled`
     (WebGL) do not, and the release notes list them.

   Capacity: one Unity process at a time on a host (the editor, its license and the test projects are
   shared, so concurrent lanes serialize them on one host-wide `flock` lock file, and a device run takes its
   device lock before the Unity lock).
   A full PlayMode suite takes tens of minutes in the host editor and about 70 minutes on the phone, so an
   RC matrix is a few hours of serial runs: start with the emulators (`run-emulator-tests.sh` builds the
   player once; `--jobs 2`, the default, runs two API levels at a time at about 3 GB RAM each, `--jobs 1` on
   smaller hosts), then the
   host-editor suites and the standalone players while the phone runs. A WSL interop outage (exit 5) is
   not a result: rerun that one step once `cmd.exe /c ver` answers, at most twice, then record it on #355.
   Exit 1 (failures), 2 (no results) and 4 (a gate) are results and are never retried away.

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
