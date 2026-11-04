# 2026-11 Player-ready PlayMode tests (#321 items 2-8, #330)

## What
Test-side fixes so the PlayMode suite means the same thing in an IL2CPP player (Stripping High) as in
the Editor, plus a bound on every `AssertAsync` body. One shipped line: `[Preserve]` on
`BouncyCastleTlsStream.MaxPendingOutputBytes`.

- **Temp path (#321 item 2).** New `Tests/Runtime/Support/TestTempPath.cs`: `Root` is
  `Path.GetTempPath()` in the Editor and `Application.temporaryCachePath` in a player (Android's
  `Path.GetTempPath()` is the unwritable `/data/local/tmp`). The player value is read on the main
  thread at startup (`[RuntimeInitializeOnLoadMethod]`), because the API is main-thread only and some
  call sites run inside `Task.Run`; a first read off the main thread falls back to `Path.GetTempPath()`
  (the device runner points `TMPDIR` at the same cache). Every `Path.GetTempPath()` in `Tests/Runtime`
  now uses it (FileDownloader, FileDownloaderResumeSafety, FileRequestBody, RecordReplay*, the four
  `IntegrationTests.Deterministic_*`). `Tests/Editor` keeps `Path.GetTempPath()` (Editor only).
- **X509Chain test-anchor cases (item 3).** `ServerCertificateVerifierTests.RequireTestAnchorVerifier()`
  skips (`[env]`) the eight cases that register a test anchor and expect a path verdict when
  `ServerCertificateVerifier.PlatformVerifierTrustsTestAnchors` is false. On Android/iOS the anchors go to
  the managed `X509Chain`, which Mono/IL2CPP cannot evaluate for the ECDSA test chains. The two
  `Invalid` cases (name-constraint exclusion, clientAuth-only intermediate) are scoped too: on players they
  passed only because `X509Chain` threw. Windows and macOS (the host editors) still run all of them. No
  verifier code changed.
- **`CoreSurfaceTests.PlumbingType_IsInternal` (item 4).** The test already used
  `typeof(UHttpClient).Assembly.GetType(name)`, so the issue's diagnosis (unqualified `Type.GetType`) was
  stale. The device `TypeLoadException` x2 matches High stripping removing internal types nothing
  references (`IL2CPPCompatibility`, `SegmentedReadStream` are named only in their own files). The cases
  are now a `TestCaseSource` of `typeof(...)` plus the expected full name; the test asserts the name, the
  Core assembly and `!IsVisible`.
- **NUL test cases (item 5).** IL2CPP truncates attribute strings at a NUL. The NUL cases moved to
  `TestCaseSource` fields built at run time in `HttpFieldValidatorTests` (value, name, HTTP/1.1
  serializer) and, same defect outside the issue's list, `RawSocketTransportInvalidFieldTests` and
  `Http2ConnectionTests.RequestHeaderValidation`.
- **Allocation gate (item 6).** `Phase19AllocationGateTests` skips (`[env]`) outside the Editor.
- **File lock (item 7).** `Restart200_PartLockedWhileTruncating_*` skips (`[env]`) off Windows: only
  Windows makes `FileShare.None` a mandatory lock.
- **Reflection (item 8).** `[Preserve]` on `BouncyCastleTlsStream.MaxPendingOutputBytes` and its setter.
- **Race-test watchdog (#330 item 1).** `SingleReaderChannelCancellationRaceTests` bounds each iteration
  (5 s) instead of the whole 2000-iteration run (30 s), and replaces the `Barrier` (two blocked pool
  threads per iteration) with a bounded spin start gate.
- **SNI capture (#330 item 2).** `ClientHelloCapture.ReadHandshakeMessage` retries a read that fails with
  `WouldBlock`/`TimedOut` until `HelloBound` (30 s), then throws `TimeoutException`. The IL2CPP Linux
  socket layer reports an expired receive timeout (EAGAIN) as "would block". `SslStreamServerNameTests`
  gives the client a longer bound than the server, so a client that never sends a ClientHello is reported
  by the server, and skips (`[env]`) the IP-literal case only when Unity's Mono SslStream sent no
  ClientHello at all; otherwise the failure message carries both the server and the client error.
- **Bounded `AssertAsync` (#330 item 3).** `Run`/`ThrowsAsync` wait with `WaitOne(DefaultBound)` (5 min)
  and throw `AssertAsyncTimeoutException`, outside the `catch (T)`, so even `ThrowsAsync<Exception>`
  fails on a hang. New overloads `Run(Func<Task>, TimeSpan)` and `ThrowsAsync<T>(Func<Task>, TimeSpan)`.
  Tests: `AssertAsyncBoundTests`.
- testing.md, "Writing tests": temp path, NUL cases, `[Preserve]` for reflected members, `[env]` gating,
  the `AssertAsync` bound.

## Decisions
- **Item 4: `typeof`, not a Core `link.xml` entry.** The type reference keeps the type in a stripped
  player without changing what ships.
- **Item 6: skip, not embed.** The budgets are calibrated on the Editor's Mono GC
  (`GC.GetTotalMemory(true)`); running them under IL2CPP would measure a different allocator against
  Editor numbers.
- **Item 7: Windows scope.** There is no platform-neutral way to make the truncate fail from a test
  without a product seam.
- **Item 8: `[Preserve]`, not a test `link.xml`.** UnityLinker does not read a package `link.xml`, and
  `TurboHttpLinkerProcessor` merges only `Runtime/**/link.xml`.
- **#330 item 2: no `[::1]` bind/connect skip.** The capture server listens on 127.0.0.1 and the client
  connects over IPv4; `[::1]` is only the TLS target host, so "cannot bind/connect [::1]" does not occur.
  The API 29 symptom is a client that sent no ClientHello within the server's 30 s receive timeout.
- **#330 item 3: 5-minute default.** The slowest legitimate bodies bound themselves at 30 s, and a
  contended API 29 emulator stretched a 10 s bound to 118 s. A hang now costs one failed test.
- **Skips in the host run.** On the Windows host editor none of the new `[env]` skips fires
  (`PlatformVerifierTrustsTestAnchors` is true, `Application.isEditor`, Windows locking). A macOS editor
  skips the file-lock case.

## Verified
All runs with Unity 2021.3.45f2. Filter (F): `CoreSurfaceTests|HttpFieldValidatorTests|Phase19AllocationGateTests|FileDownloaderResumeSafetyTests|FileDownloaderTests|FileRequestBodyTests|RecordReplay|IntegrationTests\.Deterministic|ServerCertificateVerifierTests|DesktopCertificateVerifierTests|BouncyCastleOutputBacklogTests|SslStreamServerNameTests|SingleReaderChannelCancellationRaceTests|AssertAsyncTests|AssertAsyncBoundTests|TestTempPathTests|RawSocketTransportInvalidFieldTests|SendRequest_WithInvalidRequestHeader|BouncyCastleClientHelloTests|BouncyCastleHandshakeTimeoutTests`.
- `'./Tools~/ci/check.sh'`: OK.
- Host Windows Editor, PlayMode, `--filter F`: total 250, passed 248, failed 0, skipped 2. The two skips
  are the existing `[env]` IP-literal cases of `SslStreamServerNameTests` (Mono sends the address as
  server_name, #46); none of this change's skips fires in the Editor.
- Phone (OnePlus 7, Android 12 / API 31, arm64, IL2CPP, Stripping High,
  `run-device-tests.sh --stripping High --arch arm64 --serial 80917dd4 --filter F`): total 250, passed 234,
  failed 1, skipped 15. The failure is
  `UHttpClientTests.SendAsync_CancelledRecordReplayPassthrough_PropagatesOperationCanceledExceptionWithoutHandlerError`
  (the `RecordReplay` part of the filter matched it), listed in #320 (cancellation classification, another
  lane). The 15 skips: the 8 test-anchor cases, `Restart200_PartLocked*`, the allocation gate, 3
  `DesktopCertificateVerifierTests` cases that were already desktop-only, and the 2 IP-literal SNI cases
  (#46). `CoreSurfaceTests` (29), `HttpFieldValidatorTests` (41), `BouncyCastleOutputBacklogTests`,
  `TestTempPathTests`, the Files and RecordReplay fixtures, the race test and `AssertAsyncBoundTests` pass.
- API 29 emulator (x86_64, Stripping High, `run-emulator-tests.sh --api 29 --jobs 1 --stripping High
  --filter 'SslStreamServerNameTests|SingleReaderChannelCancellationRaceTests|AssertAsyncBoundTests'`):
  total 10, passed 8, skipped 2. Both IP-literal cases captured a ClientHello and reached the #46 skip. The
  first attempt never installed a player (rsync "Cannot allocate memory" while syncing the package, host
  RAM), and the rerun passed.

## Not run
- iOS (simulator or device): not run. Items 3, 4, 6, 7 and 8 reproduced there (#321 comment); the
  fixes do not depend on the platform, but no iOS run confirms them.
- API 26 emulator under memory pressure (#330 item 1's original symptom): not run. The race test passed on
  the phone and on API 29.
- The API 29 "would block" symptom of #330 item 2 did not reproduce on an unloaded host, so the
  `TimeoutException` / `[env]` branch of `SslStreamServerNameTests` was not exercised on a device.
- Unfiltered Editor or device suites: not run (periodic host-editor run).
