# 2026-11: Phase 9 device matrix (#99)

The one record #99 asks for: every matrix row with PASS, FAIL, N/A or OPEN (not run, with the issue that owns
it). The run details are in the linked records and #99 comments; this file only collects them. All runs used
Unity 2021.3.45f2 unless noted.

## Devices and configurations

| Id | Target | Device | OS | Backend | Stripping | Record |
| --- | --- | --- | --- | --- | --- | --- |
| iOS-H | iOS arm64 | iPhone 13 Pro Max (iPhone14,3) | iOS 27.0.1 | IL2CPP | High | `2026-11-ios-device-run.md` (2026-11-22/23) |
| iOS-L | iOS arm64 | same | same | IL2CPP | Low | same |
| iOS-sim | iOS simulator x86_64 (Rosetta) | iPhone 17 Pro sim | iOS 26.4 | IL2CPP | High | same; earlier 2021.3 and Unity 6 sim rows on #99 |
| And-H | Android arm64 | OnePlus 7 (GM1903) | Android 12 / API 31 | IL2CPP | High | `2026-11-android-device-run.md`, #99 comments 2026-11-08/09 |
| And-emu | Android x86_64 emulator | AVD `default;x86_64` images on a Ryzen 7 5800X host | Android 6.0 / API 23, Android 13 / API 33 | IL2CPP | High | `2026-11-android-emulator-matrix.md`, #99 comments |
| mac | macOS arm64 standalone + Editor | Apple M2 | macOS 26.7.1 | Mono, IL2CPP | Disabled (Mono), High (IL2CPP; Mono High in #450) | `2026-11-macos-sectrust-validation.md`, `2026-11-ios-device-run.md` |
| win | Windows x86_64 standalone | Windows host editor driven from WSL2 | Windows version not recorded in the run record | Mono (IL2CPP: #1225) | Disabled | `2026-11-host-run-win64-standalone.md` |
| linux | Linux x86_64 standalone (WSL2) | WSL2 on the Windows host | WSL2 distribution not recorded in #411/#436 | Mono | Disabled | #411, #436 |

## Scenarios

| Scenario (#99) | iOS device | Android device | Emulator / simulator / standalone |
| --- | --- | --- | --- |
| Transport + WebSocket transport auto-registration (#1) | PASS: test player `DefaultTransportRegistrationTests` 9/9, `TransportBootstrapTests` (iOS-H, iOS-L). The iOS player smoke does not exist (`build-player.sh` has no iOS target). | PASS: player smoke, both builds (And-H) | PASS (all full suites) |
| link.xml / linker preservation (#8) | PASS at Stripping High in the test player (full suite incl. OAuth, JSON, coroutine bridges). Smoke not available on iOS. | PASS: smoke with and without `TURBOHTTP_BOUNCYCASTLE`, `ManagedStripped` checked (#258) | PASS: Mono High macOS ExternalNetwork after #453 |
| SslStream ALPN; Auto never falls back to unvalidated BouncyCastle (#34) | PASS: SslStream `reported=False negotiated=none` (HTTP/1.1); `HttpClient_Auto_SelectsCorrectProvider` passed | PASS (same lines, And-H) | PASS (sim, emulators) |
| BouncyCastle HTTP/2 (ALPN h2) (#36, #103) | PASS: `negotiated=h2`, `BouncyCastleProviderTests` 7/7. Through CONNECT: v1.1 (#103, default off) | PASS: h2, smoke `tls-bouncycastle` h2 | PASS: HTTP/2 rows over h2 on the API 33 emulator (#1210) |
| HTTP/1.1 and HTTP/2 against real servers | PASS: public hosts (Google, badssl.com, DigiCert, Amazon) on 3 backends, ExternalNetwork 41 passed / 0 failed | PASS: ExternalNetwork 38/0 | PASS: macOS players, emulators |
| Expect: 100-continue concurrent read/write, both TLS providers | PASS: `RawSocketTransportExpect417RetryTests` 12/12, `UHttpRequestExpectContinueTests`, BouncyCastle duplex/backlog suites (full suite) | PASS (full suite) | PASS |
| iOS ATS / Android cleartext and network security config | PASS for the cleartext policy (`CleartextTrafficPolicyTests` 17/17, `RawSocketTransportCleartextTests`, `WebSocketCleartextPolicyTests`). ATS itself does not apply to raw sockets (PlatformNotes). | PASS (`AndroidCleartextPolicyRootTests`, NetworkSecurityPolicy JNI) | N/A |
| IPv6-only network (NAT64) | OPEN: not verified in v1.0, owner decision 2026-11-23; #1283 (v1.1), test ready | OPEN: #1283 | N/A (Editor cannot prove it) |
| Network switch Wi-Fi to LTE, airplane mode | PASS: `DeviceNetworkChangeTests` (iOS-H). HTTP/2 recovery is slow (18.5 s / 26.3 s), tracked as #717 | OPEN: not run on the phone (#1284) | N/A |
| App suspend/resume: WebSocket keep-alive deadlines | PASS: `DeviceSuspendResumeTests` 5/5 launches, real process freezes (#1132). The connection is in memory (`InMemoryWebSocketPeer`): no socket, TLS or HTTP/2 | OPEN: the emulator smoke does not freeze the process (#1281); phone run #1284 | N/A: the iOS simulator does not freeze a backgrounded app |
| App suspend/resume with real sockets: HTTP/2 PING liveness, pooled reuse, wss | PASS: `DeviceRealSocketSuspendResumeTests` (iOS-H, 2026-11-24): 4 suspensions of 30 s, process frozen up to 28.9 s against 1 s / 2 s keep-alive deadlines; after every resume the pooled h2 connection (BouncyCastle, www.google.com) served the next GET in 181-221 ms with no retry, and the wss connection (echo.websocket.org) was open and echoed with no reconnect (#1289) | OPEN: #1284 | N/A |
| WebSocket (ws/wss, permessage-deflate), background/foreground | PASS: WebSocket suites incl. `WebSocketBouncyCastleTlsTests`, deflate suites. Background/foreground: keep-alive deadlines in memory (#1132) and a real wss socket across four 30 s freezes (#1289). permessage-deflate IL2CPP rows beyond the suite: v1.1 (#104) | PASS (full suite) | PASS |
| SAEA and PollSelect socket modes | PASS: channel suites, `SaeaShutdownOrderTests` 6/6; SAEA Loop384 200×3 on the sim (#1184) | PASS: 34/34 socket-mode filter (#410, #415) | PASS |
| Texture/Audio handlers, MainThreadDispatcher | PASS: handler suites, `UnityReliabilitySuiteTests`, `UnityStressSuiteTests`, `UnityPerformanceBudgetTests` | PASS (full suite) | PASS |
| Allocation/latency benchmarks on device (22a.6) | N/A on 2021.3 iOS: the allocation counter kills the player (#414); the gates skip | FAIL on arm64 budgets, documented for GA (#323, #416) | PASS on the API 33 emulator (0 B/read) |

## Device confirmations owed to other issues

| Issue | iOS | Android |
| --- | --- | --- |
| #1 auto-registration at Stripping High | PASS (test player) | PASS (smoke) |
| #8 IL2CPP smoke with the merged link.xml | PASS in the test player; smoke N/A on iOS | PASS |
| #34 Auto never falls back to BouncyCastle | PASS | PASS |
| #89 APK from a clean project (ARM64) | N/A | PASS (ARM64; ARMv7 #1257) |
| #21 HTTP/2 cancellation stress, IL2CPP ARM64 | PASS: `Http2ConnectionTests` 233/233, `StressTests` 19/19 | PASS (full suite) |
| #36 BouncyCastle concurrent read/write, Expect timeout | PASS (full suite: BouncyCastle duplex/backlog, Expect suites) | PASS |
| #67 OAuth refresh at Stripping High | PASS: `OAuth*` suites | PASS |
| #95 `GetJsonCoroutine<T>` class and struct at High | PASS: coroutine JSON bridge suites | PASS |
| #141 re-vendored BouncyCastle handshakes; AES-GCM/ChaCha20 throughput on Android | PASS (handshakes on iOS-H and iOS-L) | handshakes PASS; throughput OPEN (#141) |

## Minimum devices
- iOS ARM64 at Stripping Low and High: PASS (iOS-L, iOS-H). The only full-suite failure in both, #1251, was a test defect, since replaced by #1264.
- Oldest supported iOS: OPEN, not verified in v1.0 (owner decision 2026-11-24; #1302, v1.1). Only current iOS ran (iOS 27.0.1 device, iOS 26.4 simulator). Xcode 26.4 offers no older simulator runtime for download.
- Android ARM64: High PASS on the phone (And-H). Stripping Low has not run on the phone yet: OPEN (#1284).

## Certification matrix (#105)
`platform_matrix.py`, run on the full-suite results of 2026-11-23 (main 86bf65f + #1279):

| Cell | Row | Result |
| --- | --- | --- |
| ios-il2cpp | iPhone 13 Pro Max, iOS 27.0.1, arm64, High | 3654 tests: 3616 passed, 1 failed, 32 skipped (baseline 35); certification suites and budgets pass |
| standalone-macos | macOS IL2CPP player, arm64, High | 3654 tests: 3633 passed, 1 failed, 15 skipped (new `Player-StandaloneOSX` baseline) |
| standalone-macos | macOS Mono player, arm64, Stripping Disabled | 3654 tests: 3640 passed, 1 failed, 8 skipped |

- **Why the cells read "fail":** the one failure in each row is `RefusingLoopbackPortTests.Connect_IsRefused`, a test-helper defect on Darwin (#1286). Its fix, PR #1288, passes that test and the WebSocket proxy-failure test on all three targets. So apart from #1286, the iOS and macOS cells pass. A full rerun after #1288 merges would turn them green.
- **Earlier full runs:** the 2026-11-22 iPhone rows (High and Low, main 0065546) failed only on #1251, a test later replaced by #1264.

## Failures and their issues
Every FAIL above has an issue: #323 / #416 (arm64 allocation budgets, documented for GA) and #1286 (test helper,
fixed by #1288). Runtime finding from the same runs: IL2CPP on Darwin reports raw errno values in
`SocketException.SocketErrorCode` (#1287, v1.1, together with NAT64 #1283). The slow HTTP/2 recovery
after a network change is a PASS with a known limitation, #717.
