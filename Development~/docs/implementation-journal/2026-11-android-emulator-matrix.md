# 2026-11 Android emulator matrix: PlayMode suite across API levels (#99)

## What
- New `Tools~/unity/run-emulator-tests.sh`: boots one x86_64 AVD per API level (`turbohttp-api<N>`, wiped on
  every boot, Vulkan off, `--jobs` at a time), builds the IL2CPP x86_64 test player once and runs the
  PlayMode suite on each level. AVDs are written as plain ini files under `<cache>/emulators`
  (`%LOCALAPPDATA%\turbohttp` from WSL). Its own adb calls (boot poll, `emu kill`) are bounded by
  `timeout 30`; an emulator whose console does not answer `emu kill` is killed by pid. Each lane runs as
  its own process group, and Ctrl-C/TERM stops the lanes (run-device-tests.sh, Unity) before the emulators.
- `run-device-tests.sh`:
  - `--arch x86_64` (emulator images);
  - `--serial <id>`: Unity only builds the APK (the build modifier drops `AutoRunPlayer` and quits the
    editor on the next update, since the test runner never starts its player timeout without it); the
    script installs and launches it with `adb -s`. The phone and emulators can stay attached together;
  - `--skip-build` reinstalls that APK on another device;
  - x86_64 players get GLES3+GLES2 (phones keep the default graphics APIs);
  - result files carry the API level; every adb call of this script is bounded by `timeout 120`;
  - with `--serial`, the wait for results ends when the serial has left `adb devices` twice in a row
    (an emulator that was stopped or died), instead of polling until the 120-minute cap;
  - player liveness via `ps` instead of `pidof`;
  - a 16 MB logcat buffer per run, and the full logcat (main, system, crash) is saved when a run ends
    without results.
- testing.md step 4 and tooling.md list the emulator runner.

## Setup (host, not in the repo)
- SDK at `%LOCALAPPDATA%\turbohttp\android-sdk`, filled with the editor's `cmdline-tools\6.0` sdkmanager
  (Unity OpenJDK): `emulator` 37.2.13 and `system-images;android-{23,26,29,33,36};default;x86_64`. The
  `cmdline-tools;latest` (23.0) sdkmanager now delegates to the Android CLI and split package ids on `;`
  from cmd.exe; the 6.0 one works. Acceleration: WHPX (`emulator -accel-check`).
- The emulator accepts an SDK root only with a `platform-tools/` directory; an empty one is enough, so
  adb stays the editor's (one adb version per daemon).

## Decisions
- **Build once, install everywhere.** The IL2CPP x86_64 player is the same for every API level; the
  first level builds it, the others reuse it with `--skip-build`.
- **Vulkan off.** With Vulkan exposed (gfxstream/SwiftShader, `-no-window`), the Unity player on API 33
  froze the whole emulator: adbd and the console stopped answering and qemu used no CPU. With
  `-feature -Vulkan` the player runs on GLES.
- **GLES2 for emulator players.** API 23 and 26 images could not create a GLES3 context under
  SwiftShader; a GLES3-only development player then stops at Unity's "wait for debugger" dialog and
  never runs a test. GLES2 is added only for `--arch x86_64`.
- **`ps`, not `pidof`.** `pidof` is missing before API 24 and on the Android 6 image it printed every pid.
  The player counts as gone after two misses in a row.
- **Keep the device log.** Emulators are wiped after the run, so a run without results saves logcat
  (main, system, crash, events) next to the results; that is how the API 23 abort was found.
- **Results stay emulator results.** x86_64, no radio, no vendor TLS/network stack: they show API-level
  behavior; ARM/IL2CPP, ALPN and network rows of #99 still need the phone.

## Results (Unity 2021.3.45f2, IL2CPP x86_64, Stripping High, branch base main 957a9ad; host Ryzen 7 5800X, 16 GB)
Baseline: OnePlus 7, Android 12, arm64 (2026-11-android-device-run.md): 2484 total, 2266 passed, 209 failed, 9 skipped.

| Image | Total | Passed | Failed | Skipped | Duration | Also fail on the phone | Only on the emulator | Pass here, fail on the phone |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| API 26 (Android 8.0) | 2484 | 2287 | 188 | 9 | 60 min | 180 | 8 | 29 |
| API 29 (Android 10) | 2484 | 2245 | 231 | 8 | 107 min | 179 | 52 | 30 |
| API 33 (Android 13) | 2484 | 2303 | 172 | 9 | 46 min | 166 | 6 | 43 |
| API 23 (Android 6.0) | - | - | - | - | aborted ~1 min in | - | - | - |
| API 36 (Android 16) | - | - | 111 in the first 1786 tests (API 33: 110) | - | hung at test 1786 of 2484 | - | - | - |

API 23 and API 36 have no result file:
- **API 23 aborts (#328).** Both runs died at the same point, in `RawSocketTransportConnectionReuseTests`
  (cleartext loopback): `F art: Native thread exited without calling DetachCurrentThread` and
  `Process ... exited due to signal (11)`. `AndroidCleartextTrafficPolicy.QueryPlatform` attaches transport
  threads to the VM and never detaches them; ART on API 23 aborts when the pool retires such a thread.
  This is a product bug on the default Android cleartext path. Evidence also posted to #266.
- **API 36 hangs in `PollSelectSocketChannelTests.FullDuplex_LargeConcurrentSendAndReceive_*` (#329).** The
  last failure logged before both stalls was `HttpFieldValidatorTests.ValidateField_InvalidName_Throws("X-Test\0")`;
  the tests between it and the full-duplex test pass in about 5 s on API 33. The player process stays
  alive. `UnityMain` sleeps with no context switches in 15 s (blocked in `AssertAsync.Run`), and one
  worker thread stays runnable. The same test fails everywhere else with `SocketException: Connection
  timed out`: 108 s on the phone, 937-965 s on the emulators. Its 30 s watchdog never fires because
  `SendAsync`'s inline `Socket.Send` does not return on a full buffer. The first API 36 run ended after
  one `ps` check missed the player, with no death record; the runner now needs two misses.

The 9 skips are the phone's 9 (`[env]` or issue-marked); the skip gate reports them because the PlayMode
baseline is the Editor's (0).

### Failures not seen on the phone
They are the phone's failure modes, more often, on a contended host (two emulators, under 0.5 GB free
host RAM; API 29 ran 107 min against 46-60 for the others):
- `TcpConnectionPoolTests` loopback `TCP connect timed out after 10000ms` (API 33: 6, API 26: 2, API 29: 1):
  #322.
- `Http2ConnectionTests` / `Http2FlowControlTests` `HTTP/2 SETTINGS ACK timeout` (API 29: 46, API 26: 2),
  `no PING ACK within 10000 ms` on the >100 MB stream, cancellations in `SendRequest_Authority_*` and
  `Dispose_FailsAllActiveStreams`: #323 / #320.
- `BouncyCastleHandshakeTimeoutTests.ServerTricklingRecords_FailsAtTheHandshakeBound`: 48.6 s (API 26) and
  118.6 s (API 29) against the 10 s bound; the phone failed it at 23 s (#322).
- `SingleReaderChannelCancellationRaceTests` (API 26, both cases) "deadlocked at iteration 15/39": the 30 s
  watchdog covers all 2000 iterations and each iteration blocks two pool threads on a `Barrier`, so a
  starved thread pool trips it; it passed on the phone and on API 29/33. Test robustness, not a channel
  deadlock (#330).
- `SslStreamServerNameTests.SslStream_ClientHello_IpLiteral_SendsNoServerName("[::1]")` (API 29): the
  phone skips it (`[env]`); on API 29 the capture server's IPv6 loopback read failed with "would block"
  before any ClientHello, so the skip condition was never reached (#330).


- **Interrupting the matrix (review of #331).** Without job control bash starts `lane &` with SIGINT
  ignored, so a Ctrl-C left every lane's run-device-tests.sh (and Unity) polling a stopped emulator for up
  to 120 minutes. Lanes now start under `set -m` (own process group, SIGINT not ignored; foreground
  commands keep running without job control so Ctrl-C still reaches the script) and the EXIT/INT/TERM
  trap sends TERM to each lane group, then `emu kill` to each emulator. The lane's emulator is in the
  lane's group, so it stops even when its console is frozen.

## Follow-ups
- #328 (P1): JNI attach without detach aborts Android 6 (cleartext policy, background plugin).
- #329 (P1): PollSelect inline send does not return on a full buffer; the full-duplex test hangs a run.
- #330 (P2): race-test watchdog, `[::1]` SNI test, `AssertAsync.Run` hang bound.
- Evidence added to #322 (pool connect timeouts, BouncyCastle trickling bound) and #323 (SETTINGS ACK).

## Not run
- A complete API 23 or API 36 suite: blocked by #328 and #329.
- Stripping Low; arm64 images (ARM translation on API 30+ x86_64 images was not used).
- Emulator rows are not device validation: every #99 row stays `needs-device-validation`.
- Unity EditMode/PlayMode in the Editor were not rerun (tooling-only change); `check.sh` passed.
- The interrupt and frozen-console paths were smoke-tested on Linux with stub emulator/adb/run-device-tests
  scripts (Ctrl-C through a pty: exit 130, no lane or emulator process left; frozen `emu kill`: timed out
  and the emulator was killed by pid, lane finished in 62 s). Not rerun with real emulators or Unity, and
  not from WSL: whether a TERM/KILL of the Linux side of `emulator.exe`/`Unity.exe` stops the Windows
  processes (qemu child included) is unverified; `emu kill` stays the primary stop.
