# Platform guide

Load for TLS, sockets, IL2CPP/AOT, iOS/Android behavior, timeouts or device validation.

## TLS

- `SslStream` is the primary provider. Strict validation: only `SslPolicyErrors.None` is accepted.
  `SslProtocols.None` lets the OS negotiate; TLS < 1.2 is rejected after the handshake. ALPN goes
  through a runtime-probed `SslClientAuthenticationOptions` overload; without it the 4-argument
  overload is used with dispose-on-cancel.
- BouncyCastle (`TurboHTTP.Transport.BouncyCastle`) exists for IL2CPP platforms where `SslStream`
  ALPN is missing. **Its certificate chain validation is not implemented** (#33). Until it is, it
  must never be selected silently: Auto mode's permanent fallback on platform exceptions (#34) is a
  release blocker.
- Certificate or authentication failures never trigger a provider fallback.
- SNI and Host use `Uri.IdnHost`; IP literals get no SNI (#46).

## IL2CPP / AOT

- Banned in TurboHTTP sources (build fails): `Reflection.Emit`, `Expression.Compile`,
  `MakeGenericMethod`, `MakeGenericType`, `Activator.CreateInstance(Type)`, sync-over-async,
  `DateTime.Now`, global `ServicePointManager` TLS settings. Existing uses are listed in
  `Tools~/ci/analyzer-baseline.txt` and tracked by issues (e.g. #95 `GetJsonCoroutine`).
- Reflection targets must survive Managed Stripping Level High. `link.xml` inside a UPM package is not
  read by UnityLinker unless an `IUnityLinkerProcessor` returns it (#8); `[Preserve]` on the target
  is the reliable option.
- `[ModuleInitializer]` is not a supported Unity feature; transport registration needs a
  `RuntimeInitializeOnLoadMethod` backstop (#1).
- Fields shared across threads: `Interlocked`/`Volatile` (ARM64 reorders; 32-bit IL2CPP tears 64-bit
  reads). Use `int` flags, not `bool`, with `Interlocked`.
- Async state machines box when they complete asynchronously; keep hot `ReadAsync` paths completing
  synchronously when data is buffered.
- Doubles shared across threads are stored as `long` bits (`BitConverter.DoubleToInt64Bits`);
  reference fields use `Volatile.Read/Write`; claim ownership with `Interlocked.Exchange`.
- `ct.Register(static callback, state, useSynchronizationContext: false)`: no per-call closures or
  `object[]` state. No `goto` across variable declarations in loops (IL2CPP exception tables).
- `Math.Abs(int.MinValue)` throws even in `unchecked`; use `& int.MaxValue` for hash buckets.
- Optional providers are selected with compile-time defines (`versionDefines`), not runtime type
  probes. New generic `ValueTask<T>`/channel instantiations need a device check (link.xml is not
  reliable, #8).

## Unity main thread

- Unity APIs (`Texture2D`, `AudioClip`, `Application.persistentDataPath`, JNI) run on the main thread
  through `MainThreadDispatcher`; validate cheap arguments before dispatching.
- Never block the main thread: no `Task.Wait` in `OnDestroy`/shutdown (use `Abort()`/`Dispose()`),
  no sync-over-async in MonoBehaviours.
- Objects created on the main thread for a cancelled request are destroyed, not leaked (#94).

## Mobile

- iOS: ATS does not apply to raw sockets; cleartext policy is our responsibility (#85). Apple review
  requires IPv6-only (NAT64) networks to work. Suspension kills sockets; pooled connections may be
  half-dead on resume.
- Android: `NetworkSecurityConfig` cleartext rules are not enforced by raw sockets; network changes
  (Wi-Fi ↔ LTE) leave half-open connections. No SO_KEEPALIVE today (#47); HTTP/2 PING liveness is
  missing (#22).
- Background networking bridges (`Runtime/Unity/Mobile`, `Plugins/`) are unvalidated and must ship
  as experimental (#106).

## Timeouts (best effort on .NET Standard 2.1)

- `Dns.GetHostAddressesAsync` has no cancellation: a hung DNS lookup consumes the whole request
  timeout. Known limitation.
- `Socket.ConnectAsync` has no cancellation token: cancellation disposes the socket
  (`ct.Register(socket.Dispose)`), which can surface `ObjectDisposedException`; map it to the timeout
  or cancellation error.
- The `SslClientAuthenticationOptions` handshake overload is fully cancellable.

## Device validation

Editor PlayMode results never prove device behavior. The matrix (iOS/Android IL2CPP, stripping
levels, ALPN on both providers, NAT64, network switches, suspension) is tracked in #99; HTTP/2 through
CONNECT on BouncyCastle in #103. Mark affected issues `needs-device-validation` and record device,
OS, Unity version, backend and stripping level in the journal when you run it.
