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
- **Network steps follow the route, not `Application.internetReachability`.** In the first network run the flag
  stayed `ReachableViaLocalAreaNetwork` for three minutes after Wi-Fi was turned off. `DeviceNetworkChangeTests`
  now reads the source address of a UDP "connect" to a public resolver (nothing is sent): the Wi-Fi LAN address,
  then a cellular address, then no route. Each step also shows its instruction on the screen, because the
  phone used had vibration off. Airplane Mode drops the Mac's CoreDevice link to the phone, even with the
  cable in, so the runner keeps pulling `results.xml` until it is complete instead of taking the partial file.
  After a run that ended in Airplane Mode, iOS refused to launch the app until the phone was back online to
  verify the developer profile.
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
| Same phone, `DeviceNetworkChangeTests.SameClients_*` (one SslStream and one BouncyCastle client kept across the run; GET www.google.com, 30 s timeout) | Passed. Wi-Fi: 200 in 434 / 350 ms. Wi-Fi off, now on cellular (first request on the old pools): 200 in 10.8 s / 18.5 s. Airplane Mode: `NetworkError` in 12 ms / 26.3 s. Airplane off, back on Wi-Fi: 200 in 459 / 306 ms. An earlier attempt (it ended before the last step, see Decisions): cellular 749 / 454 ms, Airplane 10.0 s / 29.5 s. The HTTP/2 times are #717's stall, with the evidence posted there. |
| iPhone, full suite, Stripping High, main 86bf65f + this branch (2026-11-23) | 3654 tests: 3616 passed, 1 failed (#1286, test helper; fixed by #1288 and verified on the phone: refused after 3 ms), 32 skipped, min-total met |
| macOS Mono and IL2CPP players, full suite, main 86bf65f + this branch | 3654 tests each: Mono 3640 passed, IL2CPP 3633 passed; the 1 failure in each is #1286 (verified fixed by #1288). These runs set the `Player-StandaloneOSX` baseline |
| macOS Editor and Mono/IL2CPP players, provider tests (#607) | SslStream negotiated TLS 1.2 and BouncyCastle TLS 1.3 (www.google.com), recorded in PlatformNotes |
| macOS standalone players (#286), main 86bf65f + this branch: `run-tests.sh --platform StandaloneOSX --backend Mono` (Stripping Disabled) and `--backend IL2CPP` (High), `--category ExternalNetwork --filter 'TlsPublicHostTests\|BouncyCastleProviderTests'` | 25/25 on each backend, incl. main's `ValidRsaChain_Succeeds` (www.digicert.com) on SslStream, BouncyCastle and Auto, the ECDSA row and the four rejections. The amazon RSA row passed 25/25 on both backends earlier the same day. |
| Same phone, `DeviceRealSocketSuspendResumeTests` (#1289), Stripping High, main 98db52b + the #1289 branch, 2026-11-24: `run-ios-player.sh --suspend-cycles` with `cycles=4 bg=30 gap=20` | Passed. 4 process freezes, all past the keep-alive deadlines (longest 28.8 s). BouncyCastle client with HTTP/2 PING every 1 s (2 s timeout), a 1 s reuse-probe threshold and a 600 s HTTP/2 stall timeout (the held body sits unread for about 50 s per cycle). Each cycle held a dl.google.com h2 download open, unread, across the freeze (liveness PINGs armed); after each resume it delivered 4 MiB more in 3619-3657 ms. The next GET to www.google.com (the idle connection was still pooled at every resume; reuse PING first) returned 200 in 214 / 257 / 213 / 194 ms. wss to echo.websocket.org (Ping 1 s, PongTimeout 2 s) echoed every time. 0 failures, retries or reconnects. Earlier versions the same day are not counted: the first held no stream across the freeze, so it did not exercise the h2 PING loop; the second passed with the same numbers but kept the default 60 s stall timeout, about 8 s from aborting the held body. |
| iPhone, full suite, Stripping High, main 00bff45 (with #1288) + the #1289 branch, 2026-11-24 | 3798 tests: 3761 passed, 0 failed, 37 skipped (32 counted, baseline 35), min-total met. `ios-il2cpp` certification cell: pass |
| macOS Mono (Stripping Disabled) and IL2CPP (High) players, full suite, same code, 2026-11-24 | 3798 tests each, 0 failed: Mono 3785 passed / 13 skipped, IL2CPP 3778 passed / 20 skipped (within the `Player-StandaloneOSX` baseline). `standalone-macos` cell: pass |

Rows the full device suite covers (all passed): `RawSocketTransport_UploadBlockedByPeerNotReading_RequestTimeoutIsTimeout`
(#633: the stalled send ended at the 500 ms request timeout), `Il2cppStreamingSpikeTests` 7/7 (#102),
SAEA/PollSelect channel suites, `SaeaShutdownOrderTests` 6/6, `BouncyCastleHandshakeThreadPoolTests`,
Texture/Audio handlers, `MainThreadDispatcher`, `UnityReliabilitySuiteTests`, `UnityStressSuiteTests`,
`UnityPerformanceBudgetTests`, `Nat64AddressSynthesizerTests`, `DefaultTransportRegistrationTests`.
Allocation gates skip on 2021.3 iOS (#414), so no device allocation numbers.

## Not run
- NAT64: `DeviceNetworkChangeTests.Nat64Network_DnsNameAndIpv4Literal_ConnectOnBothBackends` is written (it
  checks DNS64 through `ipv4only.arpa`, then requests a name and the IPv4 literal 1.1.1.1 on both backends),
  but the NAT64 network could not be set up on the Mac (Internet Sharing needs a wired upstream), so it has not
  run. Owner decision 2026-11-23: NAT64 moves to #1283 (v1.1); v1.0 documents it as not verified
  (PlatformNotes "IPv6 and NAT64"). ATS beyond the cleartext tests.
- The HTTP/2 row tests against a row server (#1210) and an iOS player smoke (`build-player.sh` has no iOS
  target).
- The #114 short soak on iOS.
- Unity 6 arm64 simulator (no Unity 6 on the Mac).
