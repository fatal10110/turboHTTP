# 2026-11 iOS simulator run: PlayMode suite as an IL2CPP Simulator SDK player (#99)

## What
- New `Tools~/unity/build-ios-test-player.sh`: builds the PlayMode suite as an iOS IL2CPP Simulator SDK
  test player from a throwaway project that consumes a synced copy of the package, and exports the
  Xcode project. It does not run the player, because a Windows editor cannot. A Mac builds it with
  `xcodebuild` (`CODE_SIGNING_ALLOWED=NO`), installs it on a simulator and runs it. The handoff is a
  shared folder: `<dir>/xcode/`, then `READY`; the Mac writes `out/` and `DONE`. The script writes into its
  own test project (`UNITY_IOS_TEST_PROJECT`, never the package):
  - an Editor `ITestPlayerBuildModifier` + `IPostBuildCleanup` that sets IL2CPP, the stripping level, the
    bundle id `com.turbohttp.devicetests`, Simulator SDK, minimum iOS 15.0 and (Unity 6) the ARM64
    simulator architecture. It drops `AutoRunPlayer`/`ConnectToHost`, sets the output path, and exits the
    editor after the build (the Test Runner would otherwise wait for results forever);
  - a `TestRunCallback` that logs `TURBOHTTP_DEVICE_TEST <name>` on every start and
    `TURBOHTTP_DEVICE_FAIL <name>` with the message and 8 stack lines. It rewrites
    `persistentDataPath/results.xml` after every fixture (`partial="true"`) and writes the full NUnit
    tree at the end, then quits the player (`Application.Quit` -> `exit(0)` in the trampoline). A
    watchdog thread logs `TURBOHTTP_DEVICE_HANG <name>` and rewrites the partial XML with `hung="<name>"`
    when a test runs over 180 s. A hung test blocks the main thread (`AssertAsync.Run`), so nothing
    else can report it.
- testing.md "Host-editor gates" step 5 and the tooling.md table describe the run.

## Decisions
- **Unity 6 for the simulator, not 2021.3.** 2021.3.45f2's simulator player is x86_64 only
  (`Trampoline/Libraries/libiPhone-lib-amd64-il2cpp.dylib`, `libil2cpp-amd64*.a`), and there is no
  `simulatorSdkArchitecture` setting. The Mac's only runtime, iOS 26.4 on Apple Silicon, is arm64-only.
  6000.3.26f1 (Unity 6.3 LTS) has arm64 simulator libraries and `PlayerSettings.iOS.simulatorSdkArchitecture`.
  The script sets ARM64 under `UNITY_6000_0_OR_NEWER`. Results from it are labelled as not the 2021.3
  baseline. A separate test project per editor version keeps the 2021.3 project from being upgraded.
- **Build only, run elsewhere.** `-runTests -testPlatform iOS` builds the player through the Test Runner, so
  the test scene and the run settings, including `-testFilter`, are embedded. The cleanup hook exits the
  editor once the export is done. `-testFilter '!<full name>;!<full name>'` excludes tests (UTF
  `RuntimeTestRunnerFilter`), which is how runs 2-3 got past the hangs.
- **Own environment variables** (`UNITY_IOS_TEST_PROJECT` / `UNITY_IOS_PACKAGE_COPY`), as
  `run-device-tests.sh`, so `run-tests.sh`'s project never receives the player-only callback.
- **No cleartext or ATS setting in the test player.** `AllowCleartext` defaults to false in iOS players, and
  the library never consults ATS (raw sockets). The loopback tests opt in themselves (#321, PR #326).

## Results (iPhone 17 Pro simulator, iOS 26.4 (23E244), arm64, Unity 6000.3.26f1, IL2CPP High)
| Run | Package | Excluded | Finished | Passed | Failed | Skipped | End |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | main 466935d | none | n/a | n/a | 86 before the hang | n/a | hang, no XML (pre-instrumentation) |
| 2 | main 466935d | PollSelect FullDuplex | 2309 | 2202 | 96 | 11 | hang: `WebSocketBouncyCastleTlsTests.Wss_BouncyCastleBackend_EchoesText` |
| 3 | + #326 (first commit) | + that test | 2309 | 2282 | 16 | 11 | hang: `WebSocketBouncyCastleTlsTests.Wss_BouncyCastleBackend_ThroughConnectProxy_EchoesText` |
| 4 | + #326 (both commits) | PollSelect FullDuplex, whole `WebSocketBouncyCastleTlsTests` | 1242 | 1235 | 5 | 2 | hang: `ReusedConnectionFirstByteTimeoutTests.InfiniteFirstByteTimeout_DisablesTheRetry` (passed in run 3) |
| 5 | same | + that test | 1925 | 1915 | 6 | 4 | hang: `BouncyCastleHandshakeTimeoutTests.ServerTricklingRecords_FailsAtTheHandshakeBound` (passed in run 3) |

Findings and where they are tracked:
- **Hangs (#322).** `PollSelectSocketChannelTests.FullDuplex_LargeConcurrentSendAndReceive_CompletesWithoutDeadlock`:
  the main thread is blocked in `AssertAsync.Run` -> `Task.SpinThenBlockingWait`, and the app sits at 100% CPU.
  Both `WebSocketBouncyCastleTlsTests` cases (BouncyCastle WSS, direct and through a CONNECT proxy) hang past
  180 s. On Android the same tests fail (connect or handshake timeouts) instead of hanging. About 173
  tests after that fixture have not run on iOS yet.
- **iOS certificate verifier (#239, #321 item 3).** In `ServerCertificateVerifierTests`, 6 tests fail
  with "chain evaluation failed: NotSupportedException" (the managed `X509Chain` test-anchor path).
  Two more report SecTrust OSStatus -25318 / -67901 as `Invalid` where `UntrustedRoot` is expected,
  because `AppleTrustVerifier.MapEvaluationError` maps only `errSecNotTrusted` (-67843).
  `BouncyCastleChainValidationTests.Loopback_SelfSignedWithoutAnchor` then sends `bad_certificate` instead
  of `unknown_ca`. All of these fail closed.
- **Test readiness (#321).** The cleartext default caused 83 failures in run 2, fixed by #326. Also
  `CoreSurfaceTests` type lookup (2), the stripped `MaxPendingOutputBytes` setter (1), the Phase19 baseline
  file (1), and `FileDownloaderResumeSafetyTests.Restart200_PartLocked...` (1, file locking).
- **#317**: `StressTests.ConcurrencyMiddleware_OwnedLimiter_DisposedWithClient`, as in the Editor.
- **Intermittent IL2CPP read hangs (#293, #322).** Runs 4 and 5 each hung in a test that had passed in run 3:
  the HTTP/1.1 head read on a reused connection with an infinite first-byte timeout (#293), and the BouncyCastle
  handshake bound against a trickling peer (#322). Every hang so far is a socket read that the
  cancellation or `Dispose` meant to end it does not end. Excluding one test per run only reaches the
  next one. The rest of the suite (the remaining `Transport.Tls` fixtures, including `ServerCertificateVerifierTests`,
  then everything after them) ran fully only in runs 2-3.

## Not run
- A complete run: the ~173 tests after `WebSocketBouncyCastleTlsTests` have not run on iOS, because every run so far ended on an intermittent hang.
- 2021.3.45f2 on a simulator (no x86_64 runtime on the Mac); an iOS device; Stripping Low.
- Device-only rows stay `needs-device-validation`: ATS, NAT64, ALPN, suspension.
