# 2026-11 BouncyCastle host-editor verification (#206, #221, #142 audit)

Lane bc-host-verify (waves run w1101). Base: main 84b1345. Unity 2021.3.45f2 (Windows editor, Mono), driven from WSL
with `Tools~/unity/run-tests.sh` and filtered runs only.

## What changed
All changes are test-only. No runtime code changed.
- `Tests/Runtime/Transport/Tls/BouncyCastleLoopbackServer.cs`:
  - `SendCloseNotify(SslStream)` makes Unity Mono's SslStream actually emit close_notify (#206.2).
  - `TryStartTlsAsync(Func<Stream, Task>)` is an asynchronous TLS server mode that never blocks a
    pool thread (#206.1). `ReadExactAsync` and `DrainUntilClosedAsync` support it.
  - The class remarks record the Mono pool behaviour.
- `BouncyCastleOutputBacklogTests` uses `SendCloseNotify`. Its assertions are unchanged.
- `BouncyCastleHandshakeThreadPoolTests` uses the asynchronous server. The clamp, the handshake count,
  the zero-sync-I/O assertions and the 60 s kernel wait are unchanged.
- New `BouncyCastleSocketModeTests` runs BC over `SaeaStream`, `PollSelectStream` and `NetworkStream`
  with a 70000-byte echo (several records each way) (#221 AC3).
- `Tests/Runtime/WebSocket/WebSocketTestServer.cs`:
  - New optional `ServerCertificate` (TLS 1.2 SslStream, asynchronous handshake).
  - New `CreateSecureUri()`.
- New `WebSocketBouncyCastleTlsTests`: wss with `TlsBackend.BouncyCastle`, both directly and through
  `WebSocketTestProxyServer` (CONNECT) (#221 AC3).

## Root causes (#206)
Evidence: filtered PlayMode runs, plus the Unity Mono `System.dll` (`MonoBleedingEdge/lib/mono/unityjit-win32`)
decompiled with ilspycmd outside the repo.
1. `ConcurrentHandshakes_OverSaea_WithClampedThreadPool_CompleteWithoutBlockingIo`: a test assumption.
   - Cause: Mono's synchronous SslStream API is sync-over-async.
     - `MobileAuthenticatedStream.AuthenticateAsServer`, `Read` and `Write` call
       `ProcessAuthentication(runSynchronously: true).Wait()` or `StartOperation(...).Result`.
     - `InnerRead(sync: true)` is `Task.Run(() => InnerStream.Read(...))`.
     - So each blocked server read holds a pool worker, even though the server runs on a dedicated
       thread.
   - Effect: with 2x the clamp of handshakes (32 against 16 workers), the 32 server reads took every worker.
     - A diagnostic run logged `GetAvailableThreads` = 0/16 for the whole 30 s.
     - The client's SAEA completions had no thread left to run on.
     - The servers then failed at their 30 s ReceiveTimeout, and the client at its 30 s handshake deadline.
     - Unclamped, the same run took 13 s, the time Mono's thread injection needed.
   - Client side: the client is not at fault.
     - The spy counts 0 synchronous Read/Write calls.
     - A separate probe showed that pending SAEA, BeginReceive, NetworkStream and Socket-Task receives
       hold no worker on Mono.
     - The NetworkStream and PollSelect inner streams pass under the same clamp.
   - Fix: the test uses an asynchronous server, `AuthenticateAsServerAsync` plus an async handler, which
     on Mono calls `InnerStream.ReadAsync` and blocks nothing.
2. `Read_Fails_WhenQueuedOutputExceedsBound_WhileWriterIsStalled`: a test assumption.
   - Cause: `Mono.Unity.UnityTlsContext.Shutdown()` calls `unitytls_tlsctx_notify_close` only when
     `Settings.SendCloseNotify` is set.
     - `MonoTlsSettings.SendCloseNotify` is internal and defaults to false.
     - `SslStream(stream, leaveOpen)` copies the default settings into its private `settings` field.
     - `MobileTlsContext.Settings` is that same object, and it is read at Shutdown.
   - Effect: on Unity Mono, `ShutdownAsync()` puts nothing on the wire, so the client reads a bare EOF.
     `CloseInput()` then raises NoCloseNotify. That is correct client behaviour (RFC 8446 section 6.1).
   - Fix: `SendCloseNotify` sets the flag by reflection when it runs on Mono, then calls `ShutdownAsync`.
     - It throws if the member is missing, so the test cannot pass vacuously.
     - On .NET, `ShutdownAsync` always sends close_notify.

## Decisions
- Test-side fixes only. The product paths behave per RFC 8446 6.1 / RFC 5246 7.2.1 (truncation is an
  error; a received close_notify queues the reply that trips the bound) and per #169 (no sync I/O,
  no pool hold). The #36 assertions (IOException "queued output", inner stream disposed, stalled writer
  released) and the #169 assertions (0 sync I/O, completion under the clamp) are kept as they were.
- The asynchronous server shares the clamped pool but never blocks it. A client regression that held
  pool threads would still starve the clamped pool, so the test still catches one.
- Reflection on Mono internals is in test code only; on a runtime without the member it fails loudly.
- CONNECT with h2 is not run in the editor: Mono SslStream has no ALPN, so no loopback origin can
  select h2. Filed #255 (v1.0 GA); devices stay in #103.

## #142 audit (no code)
Every acceptance row maps to main 84b1345:
- `TurboTlsClient.cs` lines 26, 33-44, 96, 102, 108, 111 and 204-207.
- `BouncyCastleHostileCertificateTests` lines 35-46 and 73.
- `BouncyCastleClientHelloTests` lines 30-60.
- `BouncyCastleHandshakeTimeoutTests` lines 21 and 46.
- `BouncyCastleTruncationTests` line 26.
- `SslStreamServerNameTests` and `TlsServerNameTests`.

AC2 and AC3 are #230. All of these ran green in the BC PlayMode filter below.

## How verified
Unity PlayMode, filtered (results under `%LOCALAPPDATA%\turbohttp`):

| Run | Filter | Result |
| --- | --- | --- |
| Baseline at 84b1345 | `BouncyCastleHandshakeThreadPoolTests\|BouncyCastleOutputBacklogTests` | both fail as reported |
| After fix | the same two plus `BouncyCastleSocketModeTests` | 5/5 in 1.1 s |
| BC gate | `BouncyCastle\|TlsHostnameValidation\|TlsProviderSelector\|TlsIntegration\|TlsFallback` | 95/95, 0 skipped/ignored |
| CONNECT | `RawSocketTransportProxyTunnel` | 15/15 |
| wss | `WebSocket` | 156/156, incl. the 2 new wss/BC tests |
| PollSelect | `PollSelect\|SocketChannelLifecycle\|TcpStreamAdapter` | 15/15 |

Player scripts were compiled with a temporary editor script in the throwaway project (not committed),
using `PlayerBuildInterface.CompilePlayerScripts`:
- Targets: StandaloneWindows64, Android and iOS.
- API levels: 6 (.NET Standard 2.1) and 3 (.NET Framework 4.8).
- Result: all 18 TurboHTTP player assemblies, including `TurboHTTP.Transport.BouncyCastle` and
  `TurboHTTP.SecureProtocol`, with 0 `error CS`.

`./Tools~/ci/check.sh`: OK.

Not run: the full EditMode/PlayMode suites (they hang on #245/#246), IL2CPP player builds and devices
(#99, #103), and the tests on .NET 8/CoreCLR.

Remainder: #206 AC3 ("both tests pass in a full host-editor PlayMode run") is not met yet. Only the
filtered PlayMode runs above passed; the full run waits for the next periodic host-editor run after
#245/#246, so this PR is "Part of #206" and #206 stays open until that run is green.
