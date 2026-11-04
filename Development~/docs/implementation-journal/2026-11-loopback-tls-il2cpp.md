# 2026-11 Localhost resolves to the interface addresses on Android IL2CPP (#322)

## What
- **Root cause.** On an Android IL2CPP player, `Dns.GetHostName()` is `localhost`. The resolver answers
  `localhost` (`GetHostAddresses`, `GetHostAddressesAsync`, `GetHostEntry`) with the interface addresses
  (API 29 emulator: `[10.0.2.16, 10.0.2.15]`), and the answer contains no loopback address. Every fixture
  that sends a request to `https://localhost:<port>` through `TcpConnectionPool` or the raw-socket
  WebSocket transport therefore dialed an interface address. The fixture servers listen on
  `127.0.0.1`, so the dial got ECONNREFUSED (`mono-io-layer-error (111)`) on every TLS provider. Happy
  Eyeballs had no loopback address to fall back to. The test CONNECT proxies dial their origin with
  `TcpClient.ConnectAsync("localhost", port)`, which explains the "Proxy closed the connection before
  sending a CONNECT response" and "server closed the connection during the TLS handshake" failures. The
  30 s `BouncyCastleTruncationTests` failure was the same refusal: the server waited for a client that
  never arrived.
- **Not the cause.** The server helpers were fine:
  - the listener starts before the port is returned;
  - the PFX loads;
  - the same fixtures pass when they dial `127.0.0.1`.

  For comparison, the host Editor (Windows Mono) resolves `localhost` to `[::1, 127.0.0.1]`.
- **Fix (product).** New `Runtime/Transport/Connection/LocalhostNames.cs`:
  - `IsLocalhostName` matches `localhost` and names under `.localhost`, in any case, with or without the
    trailing dot (RFC 6761 section 6.3).
  - `GetLoopbackAddresses` returns `::1` (when `Socket.OSSupportsIPv6`) and `127.0.0.1`.

  `TcpConnectionPool.ResolveDnsAsync` and `RawSocketWebSocketTransport.ResolveDnsAsync` return these
  addresses for localhost names and do not call the resolver.
- **Fix (tests).** The CONNECT proxies (`RawSocketTransportProxyTunnelTests.ConnectProxyServer`,
  `WebSocketTestProxyServer`) dial `IPAddress.Loopback` for a localhost authority.
- **Regression tests.** New `Tests/Runtime/Transport/LocalhostResolutionTests.cs`:
  - the name-matcher cases;
  - the loopback address set;
  - pool connects to `localhost`, `LocalHost.` and `turbohttp-322.localhost` against an IPv4 loopback
    listener (the accepted peer must be loopback);
  - a WebSocket echo over `ws://turbohttp-322.localhost`.

  The `*.localhost` cases fail on the old code on hosts whose resolver does not know such names. The
  `localhost` case failed on the old code on Android IL2CPP.

## Decisions
- **Fixed in the transport, not only in the tests.** Changing the fixtures to `127.0.0.1` would have hidden
  a product defect: an app on Android IL2CPP calling `http://localhost:<port>` (a local or embedded server)
  reached the wrong interface. RFC 6761 section 6.3 says resolution libraries SHOULD answer localhost names
  with loopback and SHOULD NOT send them to a resolver. The transport does its own resolution step, so it
  takes that role.
- **Both families.** `[::1, 127.0.0.1]` is the address set the Editor resolver already returned, so
  desktop behavior and timing do not change: Happy Eyeballs orders the families, and the IPv4 attempt
  starts after the family stagger delay when `::1` refuses. A server bound only to `::1` stays reachable.
- **No change to the server helpers.** They bind `IPAddress.Loopback`, which is correct.

## Deferred
- #344 (BouncyCastle handshake deadline relies on the read ending after Dispose) still owns the API-33
  timing cases if they recur. They passed on API 29 once the client connected (`BouncyCastleHandshakeTimeoutTests`,
  `HandshakeThreadPool`, `OutputBacklog`, `SocketMode`).

## How verified
- `'./Tools~/ci/check.sh'`: OK.
- Android emulator, API 29, x86_64 IL2CPP, Stripping High: the #322 fixture list plus `LocalhostResolutionTests` and
  `WebSocketProxyTests` passed, 138/138. Before the fix, the same image failed 40 tests, all on the
  `localhost` path.
- Host Editor PlayMode, same filter: 138/138.
- Android emulator API 33: not run yet. Twice the emulator refused to boot ("Running multiple emulators with
  the same AVD") because an orphaned `turbohttp-api33` qemu process was holding the AVD. That process was
  started at 10:25 outside this lane, and adb did not list it. It was left alone. The PR records the
  result once the run is possible.
  - Rechecked in review round 1: Windows `Get-Process` still lists PID 33104
    (`qemu-system-x86_64-headless -avd turbohttp-api33 -port 5560`, started 10:25:42). Its parent process is
    gone and the run that started it (`emulators/run-20261104-102542`) finished with exit 0 at 10:34 without
    stopping it. WSL `ps` cannot see Windows processes, so a WSL-side check reports 0 processes. Stopping
    another run's process needs the owner, so the API 33 run waits for the owner to stop it.
- Not run: the phone (arm64), the iOS simulator, unfiltered suites (host-editor periodic run).
