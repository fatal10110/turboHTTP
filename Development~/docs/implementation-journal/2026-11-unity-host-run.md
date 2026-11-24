# 2026-11: Unity host-editor runs

Running log of the periodic host-editor runs (testing.md, "Host-editor gates"). The October runs are in
`2026-10-unity-host-run.md`. Unity 2021.3.45f2, Windows editor driven from WSL, unless a section says otherwise.

## 2026-11-22: after waves run w1122 (main b842808)

Waves run w1122 merged #1249, #1253, #1254, #1255 and #1256.

- EditMode: 87 total, 87 passed, 0 failed, 0 skipped.
- PlayMode: 3612 total, 3606 passed, 1 failed, 3 counted skips (baseline 3), 2 not-selected `[Explicit]`.
  - `RawSocketTransportProxy407Tests.ForwardProxy_BasicChallenge_FirstAttemptHasNoCredentialsAndRetriesOnceWithBasic`
    failed in `TcpListener.Start` with WSAENOBUFS, 53 s into the run. The fixture passed 25/25 twice when
    run alone. Filed #1259 (Windows port churn or a socket leak under the full suite).
- ExternalNetwork: 48 total, 38 passed, 0 failed, 10 `[env]` skips: 8 HTTP/2-over-TLS row tests without
  `TURBOHTTP_H2_ROWS_URL`, 2 "SslStream has no ALPN on this runtime".
- UniTask (`--with-unitask --filter UniTaskCancellationTests`): 6/6.
- Players:
  - win64-mono: PASS (7 rows, `bc=absent`).
  - win64-mono, Stripping High, `--network`: PASS (`tls-sslstream` example.com 200).
  - android-il2cpp, with and without `--bouncycastle`: build PASS (not run on a device).
  - win64-il2cpp, Stripping High, `--network`. The Windows 10 SDK (10.0.28000.0) and MSVC are now on the host.
    - Without the define: PASS, `bc=absent`, neither BouncyCastle assembly in `ManagedStripped`.
    - With `--bouncycastle`: PASS, `bc=present`, `tls-bouncycastle` example.com 200 over h2, and the four
      `h2-*-bouncycastle` rows.
    - #1225 closed; the win64-il2cpp row recorded on #1210.
- Win64 IL2CPP standalone test player (`run-tests.sh --platform StandaloneWindows64 --backend IL2CPP`, first
  complete IL2CPP run): 3612 total, 3603 passed, 2 failed, 5 counted skips (baseline 5), 2 not-selected
  `[Explicit]`, 248 s.
  - `Http11BodySourceAbortDuringReadTests.Abort_PendingReadWithCallerToken_DoesNotLeaveTheReaderOnTheCallersToken`:
    fails 3 of 3 on this player (also in two filtered reruns); it passes in the Editor and the other players.
    Recorded on #1251, which moved to v1.0 GA.
  - `WebSocketProxyTests.ProxyConnectionFailure_ThrowsProxyConnectionFailed`: `Timeout` after 15 s instead
    of `ProxyConnectionFailed`. It passed 2 of 2 filtered reruns. The test reuses a released ephemeral port,
    which another test can take under load. Recorded on #1259.
  - The Mono backend's complete run (3603 on 2026-11-16) is recorded on #1219 / #1243. With this IL2CPP run,
    both Win64 backends have passed the skip and total gates (#1219).

## 2026-11-22: release commit 69ad7b4 (v1.0.0-pre.2)

The release-checklist step 4 gates on the version-bump merge commit (#1260):

- EditMode: 87/87, 0 skipped.
- PlayMode: 3612 total, 3607 passed, **0 failed**, 3 counted skips (baseline 3), 2 not-selected `[Explicit]`, 144 s.
- ExternalNetwork: 38 passed, 0 failed, 10 `[env]` skips (as above).
- Players: win64-mono PASS; android-il2cpp build PASS; android-il2cpp `--bouncycastle` build PASS.
- `run-samples.sh`: Sample01-05 passed.
- Install check: a new 2021.3 project with `https://github.com/fatal10110/turboHTTP.git#v1.0.0-pre.2`
  resolved the package to 69ad7b4 and compiled every TurboHTTP assembly with 0 errors.
- The tag `v1.0.0-pre.2` was pushed on 69ad7b4; the Release workflow created the GitHub pre-release.

`skip-baseline.txt` min-totals raised from these runs: EditMode 71 -> 87, PlayMode 2923 -> 3612,
Player-StandaloneWindows64 3603 -> 3612.

Not run: the iOS simulator and device (#99), the Android emulator and phone suites at these commits,
the certification matrix (release candidates only).

## 2026-11-23: after waves run w1123 (main 628c1b2)

Waves run w1123 merged #1264, #1267, #1268, #1269 and #1270.

- EditMode: 87/87, 0 skipped.
- PlayMode: 3625 total, 3618 passed, **0 failed**, 3 counted skips (baseline 3), 4 not-selected `[Explicit]`, 145 s.
- ExternalNetwork: 51 total, 41 passed, 0 failed, 10 `[env]` skips (the 8 HTTP/2 row tests without a row server,
  2 "SslStream has no ALPN"). The new `ValidRsaChain_Succeeds` rows (#1269) passed on all three backends.
- Win64 IL2CPP standalone test player: 3625 total, 3615 passed, 1 failed, 5 counted skips (baseline 5), 514 s.
  - `WebSocketProxyTests.ProxyConnectionFailure_ThrowsProxyConnectionFailed`: the released-port race again,
    2 of 2 full Win64 IL2CPP runs. #1259 moved to v1.0 GA.
  - The #1251 test, which failed 3/3 on this player before #1264, passed.
- Android emulators, x86_64 IL2CPP, Managed Stripping High (`run-emulator-tests.sh --api 33,23 --jobs 1`):
  - API 23: 3625 total, 3604 passed, 0 failed, 17 counted skips (baseline 17), 345 s.
  - API 33, run 1: 3591 passed, 13 failed, 1591 s. All 13 failures are WebSocket tests in one window
    (11:34-11:55 UTC): async tests slowed to tens of seconds or hit their 300 s bound, while synchronous tests
    stayed at 0 s, and the run recovered afterwards. That points at thread-pool starvation in the player.
    Not reproduced: the WebSocket tests alone passed 360/360 in 34 s on API 33, and a second full run had no
    such window.
  - API 33, run 2: 3602 passed, 2 failed, 21 skipped (17 counted), 417 s.
    - `Http2ConnectionTests.StreamLifecycle_StreamingResponseMoreThan100MB_ReadsToEndWithDefaultOptions`:
      `OperationCanceledException` at its fixed 180 s bound (passed in run 1 and on API 23). Filed #1272.
    - `WebSocketMetricsTests.Metrics_CaptureConcurrentSendReceiveTraffic`: `Expected 20, was 19`. The receive
      loop enqueues a message before it counts it. Filed #1271.
  - Android rows from these runs were posted on #633, #846, #821, #162, #344, #364, #1184 and #1261 (Android
    halves pass), and on #1169 (`UHttpClientPolicyTests` 20/20 on both levels).
- Android player smoke on the API 33 emulator (`build-player.sh --target android-il2cpp --stripping High --run`):
  PASS, including `android-cleartext-policy PASS #875 PlatformPolicy installed`. #875 closed.

`skip-baseline.txt` min-totals raised from these runs: PlayMode 3612 -> 3625, Player-Android 2628 -> 3625,
Player-StandaloneWindows64 3612 -> 3625.

Not run: the Win64 Mono player, the API 26/29/36 emulators, the phone, the iOS simulator and device (#99), and
a suspend/resume run (#1132 needs a harness that backgrounds the player).

## 2026-11-24: API 33 emulator stall diagnosis (#1297, branch wf/emulator-stall-diagnosis)

Android emulator, x86_64 IL2CPP, Managed Stripping High (`run-emulator-tests.sh --api 33 --jobs 1`), with the
new `TURBOHTTP_DEVICE_HEARTBEAT` probe (15 s lines: longest 1 s wait of a dedicated thread, thread-pool and
main-thread probe waits, available pool threads). Nothing else ran on the host.

- Run 1 (65d07ff, probe only): 3795 total, 3772 passed, 0 failed, 241 s. The probe flagged thread-pool
  starvation from 02:49:42 to 02:50:28 UTC: pool work items waited up to 30.6 s while the 1 s wait stayed at
  1003 ms, so the guest was running on time. Pool minimum 4 workers. Windows host CPU 56 %. The native dump
  (`debuggerd -b`, symbolized with the build's `libil2cpp.sym.so`) showed 24 pool workers idle and 9 blocked in
  `SemaphoreSlim.Wait` <- `BlockingCollection.TryTake` <- `BlockingStream.ReadAsyncCore` (the HTTP/2 and
  WebSocket tests' in-memory duplex stream, `Task.Run` around an infinite `TryTake`). Each connection a test
  left reading held one pool thread, and the IL2CPP pool adds threads slowly. The cause is ours (test helper);
  it explains the SETTINGS ACK, WebSocket and loopback timeouts of the earlier long stalls.
- Fix: `BlockingStream` awaits a `SemaphoreSlim` and holds no thread while a read is pending.
- Run 2 (fix): 3798 total, 3775 passed, 0 failed, 194 s; pool probe max 14 ms, no stall.
- Run 3 (fix): 3798 total, 3775 passed, 0 failed, 190 s; pool probe max 70 ms, no stall.
- One earlier attempt of run 2 did not boot: a WSL interop outage (`UtilAcceptVsock ... accept4 failed 110`)
  kept `emulator.exe` from starting. It is not counted.

The 20-55 minute form did not recur in these three runs. If a stall comes back, the run prints
`STALL SUSPECTED (<kind>)` and keeps a dump as `stall-Android-<tag>-<ts>-<n>.txt` in the cache.

## 2026-11-23/24: after waves runs w1124-w1127 (main b5caa65, 35043fe, 00bff45)

Waves runs w1124 (#1274, #1275, #1276, #1277), w1125 (#1282, #1285), w1126 (#1292, #1293, #1295) and w1127
(#1298, #1299). Emulators: x86_64 IL2CPP, Managed Stripping High, one at a time. Nothing else ran on the host.

- `--suspend` player smoke (#1132), API 33 emulator:
  - main 13fb048 (before #1282/#1285), 2 runs: row PASS, but `max-ping-gap=2.0s` over 34-46 s in the background
    (the process kept running) and `resume-signal=no`: TurboHTTP.Unity.WebSocket.dll was missing from
    `ManagedStripped`. Filed #1281 and #1280.
  - main b5caa65, 2 runs: `freeze=root frozen=10s; background=11.4s max-ping-gap=11.5s`,
    `ping-in-flight-at-pause=yes`, `resume-signal=yes`, `grace-waits=0`, connection Open with a working echo.
    #1280 and #1281 closed; Android half of #1132 recorded on the issue.
- main b5caa65:
  - API 33, 2 runs: 3632 and 3633 of 3656 passed. `RefusingLoopbackPortTests.Connect_IsRefused` failed 2/2
    (`SocketErrorCode` 111, raw errno: #1290); `SocketChannelLifecycleTests.Saea_Dispose_CompletionNeverFires...`
    failed once (#1291).
  - EditMode 87/87. PlayMode: run 1 lost to a WSL interop outage (exit 5); run 2 3648/3656 with
    `SaeaShutdownOrderTests.Dispose_ReceiveNeverEnds...` failing once (#1291); run 3 3649/3656, 0 failed.
  - Win64 IL2CPP player: 3646/3656, `ProxyConnectionFailure_ThrowsProxyConnectionFailed` timed out again at 15 s
    (3 of 3 full runs). Cause found in w1126: libil2cpp's Windows `select()` holds 64 sockets, so a refused
    connect past the 64th registered socket is never reported (#1295; reads: #1294, v1.1).
- main 35043fe:
  - EditMode 87/87. PlayMode, three consecutive runs: 3786/3794 passed, **0 failed**, 8 skipped each (#1259 closed).
  - Win64 IL2CPP player: 3784/3794 passed, **0 failed**.
  - API 33, run 1: 40 failed in a 55 min stall that began at 00:44:54 UTC (SETTINGS ACK, TCP connect and TLS
    handshake timeouts across fixtures; 3543 s against 223 s). Filed #1297, found and fixed in w1127 (see the
    stall diagnosis section above). Run 2: 3769/3794, 2 failed (#1296: a raw-errno assertion in
    `ConnectFailureWatchTests`, and `Http2ConnectionTests.Dispose_FailsAllActiveStreams`, a product race fixed by
    #1298).
  - #1290 probe (API 33): refused connect `SocketErrorCode=111 ErrorCode=111 NativeErrorCode=111`, normalized
    `ConnectionRefused`; DNS failure `HostNotFound (11001)`; unreachable connect: no result within 5 s.
- main 00bff45 (final set):
  - API 33, run 1: 3798 total, 3775 passed, **0 failed**, 23 skipped, 190 s; heartbeat: pool probe max 104 ms,
    no stall.
  - API 33, run 2: 3775 passed, **0 failed**, 185 s; pool probe max 53 ms, no stall.
  - API 23: 3775 passed, **0 failed**, 219 s; heartbeat: none logged (#1300).
  - EditMode 87/87. PlayMode 3790/3798 passed, **0 failed**, 8 skipped, 165 s.
  - Win64 IL2CPP player: 3788/3798 passed, **0 failed**, 10 skipped, 252 s.

`skip-baseline.txt` min-totals raised from the 00bff45 runs: PlayMode, Player-Android and
Player-StandaloneWindows64 3625 -> 3798.

Not run: the Win64 Mono player, the API 26/29/36 emulators, the phone, and the iOS rows (owner's Mac session).
