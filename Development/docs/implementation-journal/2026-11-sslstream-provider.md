# 2026-11 sslstream-provider (#171, #172, #175, #35)

Wave run w1102, lane sslstream-provider. One PR.

## What

- **#171, one authentication per SslStream.** `SslStreamTlsProvider.WrapAsync` used to catch
  `InvalidOperationException` (among reflection types) from the ALPN `AuthenticateAsClientAsync`
  call and then run the plain handshake on the same `SslStream`, writing a second ClientHello on a
  used stream.
  - The ALPN options are now built by reflection before the `SslStream` exists
    (`CreateAlpnOptions`). Only a failure there, which happens before any byte is written, can fall
    back to the plain handshake (http/1.1 offered, RFC 7301 section 3.1) or end in
    `PlatformNotSupportedException` (h2 only).
  - `IsAlpnSetupFailure` is the documented filter: `TargetInvocationException`,
    `MemberAccessException`, `TypeLoadException`, `MissingMethodException`,
    `PlatformNotSupportedException`. `InvalidOperationException` was removed: from the handshake it
    is an SslStream state error, not a missing capability.
  - `StartAuthentication` is the single authentication call; whatever it throws propagates and
    the existing catch disposes the stream.
- **#172, no unobserved connect/handshake task after cancellation.** New
  `Runtime/Transport/Internal/AbandonableTask.cs`. `WaitAsync(task, ct)` races the task against a
  registration-backed signal and releases the registration on every path. On cancellation it
  attaches an observer (`ContinueWith(OnlyOnFaulted | ExecuteSynchronously)` reading
  `Exception`, the same pattern as `TcpConnectionPool.ResolveDnsAsync`) and throws
  `OperationCanceledException`. The caller's catch then disposes the socket or stream, which ends
  the task.
  - `SslStreamTlsProvider`: both auth paths use it. The old `Task.Delay(Timeout.Infinite, ct)` race
    (unobserved auth task, and a timer registration left on a live token) is gone.
  - `HappyEyeballsConnector.ConnectEndpointAsync`: the hand-written TCS race is replaced by the
    helper, so the abandoned `Socket.ConnectAsync` task is observed.
- **#175, shared ALPN array.** `Runtime/Transport/Tls/AlpnProtocols.cs` (`H2AndHttp11`), used by
  `TcpConnectionPool` (was `new[] { "h2", "http/1.1" }` per secure connect) and the CONNECT tunnel in
  `RawSocketTransport` (`s_connectTunnelAlpnProtocols` removed). The WebSocket transport keeps its
  empty ALPN list on purpose.
- **#35.** Main already preserves the SslStream ALPN types under `System` (`Runtime/Transport/link.xml`)
  and has the EditMode test. See "How verified" for the IL2CPP build check.

## Files

- `Runtime/Transport/Tls/SslStreamTlsProvider.cs`, `Runtime/Transport/Tls/AlpnProtocols.cs` (new)
- `Runtime/Transport/Internal/AbandonableTask.cs` (new)
- `Runtime/Transport/Connection/HappyEyeballsConnector.cs`, `Runtime/Transport/Tcp/TcpConnectionPool.cs`,
  `Runtime/Transport/RawSocketTransport.cs`
- Tests: `Tests/Runtime/Transport/Tls/SslStreamSingleHandshakeTests.cs`,
  `Tests/Runtime/Transport/AbandonableTaskTests.cs`, `Tests/Runtime/Transport/UnobservedTaskProbe.cs`,
  `Tests/Runtime/Transport/TcpConnectionPoolAlpnArrayTests.cs` (new);
  `Tests/Runtime/Transport/Http1/RawSocketTransportProxyTunnelTests.cs` (reads the shared array).

## Decisions

- #171 "reconnect with a fresh socket": not done. The provider only receives a connected inner stream,
  and after a handshake exception nothing proves the stream is unused. The capability decision is
  already static (`_alpnSupported`, static ctor); a setup failure before the stream exists is the
  only fallback. A handshake failure propagates to the pool, which reports it (fail-closed Auto, #34).
- #172: race and observe, not `ct.Register(Dispose)` and await. Disposing during a handshake is what
  the catch already did; waiting for the handshake to end after dispose could hang on a runtime
  where dispose does not end it promptly.
- Test seam: internal ctor `SslStreamTlsProvider(bool simulateNoAlpn, Func<SslStream, CancellationToken, Task> handshakeOverride)`
  replaces the single authentication call (same style as the existing `simulateNoAlpn` seam).
- `UnobservedTaskProbe` faults a control task nobody observes; when the runtime does not report it,
  the unobserved-task tests are ignored as environment instead of passing vacuously.

## Deferred

- #35 device row (HTTP/2 negotiated over SslStream on a device): `needs-device-validation`, #99.
  #35 stays open ("Part of").
- #303 (v1.1): the ALPN options still go through reflection and
  `MakeGenericType(List<SslApplicationProtocol>)`. The linker keeps the members, but nothing proves
  IL2CPP generated AOT code for that instantiation, and `ExecutionEngineException` is not a setup
  failure. Not changed here.
- No allocation gate covers a TLS handshake (`Tests/Runtime/Performance/*AllocationGate*` measure
  the request/response paths). #175 AC2 is covered by reference equality in
  `TcpConnectionPoolAlpnArrayTests`.

## How verified

- `'./Tools~/ci/check.sh'`: OK. The shadow build does not compile `Tests/`; the first Unity run
  caught a missing `using TurboHTTP.Core;` in a new test.
- Unity 2021.3.45f2 host editor, PlayMode, filter
  `SslStreamSingleHandshakeTests|AbandonableTaskTests|TcpConnectionPoolAlpnArrayTests|SslStreamTlsProviderFailClosedTests|HappyEyeballs|TcpConnectionPoolTlsFallbackTests|ConnectTunnelAlpnProtocols`:
  38/38 passed, 0 skipped (the `UnobservedTaskProbe` control was reported, so the unobserved-task
  tests ran for real).
- Without the fix: with `Observe(task)` commented out in `AbandonableTask.WaitAsync`, filter
  `CancelledThenTaskFaults|CancelledPlainHandshakeFaultsLater`: 2/2 failed ("faulted after
  cancellation and nobody observed it"). The #171 test was not run against the old structure (the
  seam did not exist). On the old code the `InvalidOperationException` was caught and the plain
  `AuthenticateAsClientAsync` ran on the same stream, so the exception type and write-count
  assertions fail.
- PlayMode filter `RawSocketTransportProxyTunnelTests|SslStreamIpIdentityTests|SslStreamServerNameTests|TlsNegativeSuiteTests`:
  58/60 passed. The 2 failures are `SslStream_ClientHello_IpLiteral_SendsNoServerName`
  (UnityTls sends SNI for IP literals), already in `2026-10-unity-host-run.md`.
- Test hazard found: a seam handshake that does `await Task.Yield()` while the test blocks the
  Unity main thread deadlocks (the continuation is posted to the UnitySynchronizationContext). The
  seam tests now call `WrapAsync` inside `Task.Run`.
- #35 AC1: `'./Tools~/unity/build-player.sh' --target android-il2cpp` (IL2CPP, Managed Stripping
  High): PASS. The ManagedStripped `System.dll` (read with System.Reflection.Metadata) keeps
  `SslClientAuthenticationOptions` (.ctor, TargetHost, EnabledSslProtocols, ApplicationProtocols
  get/set), `SslApplicationProtocol` (Http2, Http11, ToString), `SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)`
  and `SslStream.NegotiatedApplicationProtocol`. The merged `TurboHTTP.link.xml` the linker received
  names them under `<assembly fullname="System">`.
- Not run: the full EditMode/PlayMode suites (periodic host-editor run), win64-il2cpp, any
  device run (h2 over SslStream on a device: #99), the runtime ALPN probe in a player (#303).
