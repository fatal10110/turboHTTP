# 2026-11: iOS test player from a Mac, the iOS simulator gate and the first iPhone run (#99, #1132)

## What
- `Tools~/unity/build-ios-test-player.sh` runs on a Mac: it finds iOS Build Support in the Hub layout
  (`<version>/PlaybackEngines/iOSSupport`), and `--team <id>` (or `TURBOHTTP_IOS_TEAM`) makes a signed
  device build (Device SDK, automatic signing, bundle id `com.turbohttp.devicetests.<team>` so a free
  personal team can provision it). The player keeps the screen awake during a run (`Screen.sleepTimeout`;
  an auto-lock mid-run backgrounded the player and iOS killed it with signal 9), and the script drops the
  staged Xcode copy after the handoff (a second ~1 GB project).
- New `Tools~/unity/run-ios-player.sh`: builds the exported project with `xcodebuild`, installs it on a
  simulator (`simctl`) or a connected device (`devicectl`), runs each launch under a time bound, pulls
  `Documents/results.xml` to `<cache>/results-iOS-<sim|device>-<ts>.xml` and applies the
  `Player-iOS-2021.3`/`Player-iOS` gates (`--full` for an unfiltered build). `--suspend-cycles` drives
  `DeviceSuspendResumeTests`: after `TURBOHTTP_SUSPEND_READY` it opens Settings for 10 s and brings the
  player back, the number of times the player asks.
- `run-tests.sh --platform StandaloneOSX --backend Mono|IL2CPP`: the Win64 standalone path for a Mac
  editor (`results-StandaloneOSX-<backend>-<ts>.xml`).
- `Tests/Runtime/Unity/DeviceSuspendResumeTests.cs` (`[Explicit]`, `DeviceSuspend`): a WebSocket
  keep-alive (Ping 1 s, PongTimeout 2 s) whose peer answers every Ping after 1.5 s, across five real
  suspensions. A background heartbeat thread records process freezes (a paused player loop is not a
  freeze); the test requires five resumes, a freeze longer than PongTimeout, at least one freeze that
  started with a Ping outstanding, the connection open after every resume, and a text frame delivered
  after the last one.
- `skip-baseline.txt`: `min-total Player-iOS-2021.3 3612`.

## Decisions
- **No uninstall between device launches.** A first version uninstalled the app before each launch to
  avoid pulling a stale `results.xml`. With a free team, iOS drops the developer-profile trust when the
  developer's last app is removed, so the next launch was refused. The player already deletes
  `results.xml` in `RunStarted`; the runner now discards the pulled file when the console has no
  `TURBOHTTP_DEVICE_RUN started` line.
- **The simulator cannot validate suspension.** On the iOS 26.4 simulator the backgrounded player's
  process keeps running (0 heartbeat freezes in 4 launches); only the player loop stops. The test fails
  there with that message, by design; the device rows below are the evidence.
- **Unity 2021.3 for the simulator.** No Unity 6 editor on the Mac, so the simulator player is x86_64
  under Rosetta 2, as in the earlier 2021.3 rows.

## Results
Host: Apple M2, macOS 26.7.1, Xcode 26.4, Unity 2021.3.45f2, IL2CPP. Code: main 0065546 plus this branch,
with local copies of three test changes that main took meanwhile in parallel PRs: the ColdBurst cleartext
opt-in (identical to #1264), the SAEA Loop384 variants (same logic as #1270, other test names) and an RSA
row against www.amazon.com (main's #1269 row uses www.digicert.com; its macOS player rerun is in the table).
The #1251 failures below predate #1264, which replaced that test.

| Row | Result |
| --- | --- |
| iOS simulator, iPhone 17 Pro, iOS 26.4, x86_64, Stripping High, full suite, 2 launches | 3612 tests: 3577 / 3576 passed, 4 / 5 failed, 29 skipped (baseline 34). Failures: the 3 ColdBurst tests (#1261, fixed), #1251 (both launches), #169 (launch 2 only, which overlapped Unity builds on the host). No hangs. |
| iPhone 13 Pro Max (iPhone14,3), iOS 27.0.1, arm64, Stripping High, full suite | 3615 tests: 3580 passed, 1 failed (#1251), 29 skipped, min-total met. Includes the #1261 fix (fixture 11/11). |
| Same phone, `--category ExternalNetwork --integration` | 51 tests: 41 passed, 0 failed, 10 skipped (8 HTTP/2 row tests: no row server; 2: SslStream has no ALPN). `TlsPublicHostTests` 18/18 incl. `ValidRsaChain_Succeeds` on all three backends (#286). BouncyCastle `negotiated=h2`; SslStream `reported=False negotiated=none` (HTTP/1.1). |
| Same phone, `DeviceSuspendResumeTests`, 5 launches | 5/5 passed: 25 process freezes (8.8-9.2 s), 10 started with a Ping outstanding, 3 grace re-checks, the connection open through every resume. The first 3 launches (Stripping High) ran the version whose post-resume text check the pump could take; the last 2 (Stripping Low) ran the fixed check, and the peer received the text. One more launch right after the profile re-trust saw no resume (the phone did not become active) and is not counted. |
| Same phone, Stripping Low, full suite | 3615 tests: 3580 passed, 1 failed (#1251), 29 skipped, min-total met. |
| macOS standalone players (#286), main 86bf65f + this branch: `run-tests.sh --platform StandaloneOSX --backend Mono` (Stripping Disabled) and `--backend IL2CPP` (High), `--category ExternalNetwork --filter 'TlsPublicHostTests\|BouncyCastleProviderTests'` | 25/25 on each backend, incl. main's `ValidRsaChain_Succeeds` (www.digicert.com) on SslStream, BouncyCastle and Auto, the ECDSA row and the four rejections. The amazon RSA row passed 25/25 on both backends earlier the same day. |

Rows the full device suite covers (all passed): `RawSocketTransport_UploadBlockedByPeerNotReading_RequestTimeoutIsTimeout`
(#633: the stalled send ended at the 500 ms request timeout), `Il2cppStreamingSpikeTests` 7/7 (#102),
SAEA/PollSelect channel suites, `SaeaShutdownOrderTests` 6/6, `BouncyCastleHandshakeThreadPoolTests`,
Texture/Audio handlers, `MainThreadDispatcher`, `UnityReliabilitySuiteTests`, `UnityStressSuiteTests`,
`UnityPerformanceBudgetTests`, `Nat64AddressSynthesizerTests`, `DefaultTransportRegistrationTests`.
Allocation gates skip on 2021.3 iOS (#414), so no device allocation numbers.

## Not run
- NAT64 (an IPv6-only network from macOS Internet Sharing), ATS behavior beyond the cleartext tests,
  Wi-Fi/LTE switch and airplane mode: need manual network setup.
- The HTTP/2 row tests against a row server (#1210) and an iOS player smoke (`build-player.sh` has no iOS
  target).
- The #114 short soak on iOS.
- Unity 6 arm64 simulator (no Unity 6 on the Mac).
