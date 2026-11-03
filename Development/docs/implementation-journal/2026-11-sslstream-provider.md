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
