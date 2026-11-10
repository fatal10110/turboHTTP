# Platform Support & Compatibility

TurboHTTP is designed to run reliably across major Unity platforms, supporting both Mono and IL2CPP scripting backends.

## Supported Platforms Matrix

| Platform | Scripting Backend | Status | Notes |
| :--- | :--- | :--- | :--- |
| **Windows** | Mono / IL2CPP | ✅ Supported | TLS 1.3 where the OS offers it. HTTP/2 with `TlsBackend.BouncyCastle` (see below). |
| **macOS** | Mono / IL2CPP | ✅ Supported | TLS 1.3 where the OS offers it. HTTP/2 with `TlsBackend.BouncyCastle` (see below). |
| **Linux** | Mono / IL2CPP | ✅ Supported | Verified on Ubuntu 20.04+. |
| **iOS** | IL2CPP | ✅ Supported | Requires AOT-safe configuration. |
| **Android** | IL2CPP | ✅ Supported | ARM64/ARMv7. Restrictions on cleartext apply. |
| **WebGL** | IL2CPP | ❌ Unsupported in v1.0 | Browsers provide no raw sockets. A WebGL player build fails at build time with an explanatory error instead of failing every request at runtime. Support is planned for a later release. |
| **Console** | IL2CPP | ⚠️ Experimental | Socket implementation varies by platform SDK. |

## Platform Defaults

TurboHTTP automatically allows the runtime platform to determine optimal defaults.

| Feature | Desktop / Editor | Mobile (iOS/Android) |
| :--- | :--- | :--- |
| **Request Timeout** | 30 seconds | 45 seconds |
| **Max Concurrency** | 16 requests | 8 requests |
| **Pooled connection idle timeout** (`ConnectionPoolOptions.ConnectionIdleTimeout`) | 120 seconds | 45 seconds (needs device validation, #99) |
| **Reused-connection first-byte timeout** (`ConnectionPoolOptions.ReusedConnectionFirstByteTimeout`) | 10 seconds | 10 seconds |
| **TCP keepalive** (`SO_KEEPALIVE`) | on, OS probe timing (Unity's .NET profile exposes no timing options) | on, OS probe timing (needs device validation, #99) |
| **TLS Provider** | SslStream (an explicit `TlsBackend.BouncyCastle` verifies chains with crypt32 on Windows and SecTrust on macOS; unsupported on Linux, where it rejects every certificate) | SslStream (Auto never falls back to BouncyCastle; define `TURBOHTTP_BOUNCYCASTLE` and select `TlsBackend.BouncyCastle` explicitly to use it; on devices it verifies chains with SecTrust / the Android trust manager; device validation pending) |

### Overriding Defaults

```csharp
var options = new UHttpClientOptions
{
    // Force a global timeout
    DefaultTimeout = TimeSpan.FromSeconds(60)
};
```

To override connection concurrency, configure the connection pool:

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
options.ConnectionPool.MaxConnectionsPerHost = 4;
```

A reused HTTP/1.1 connection that sends no response byte within `ReusedConnectionFirstByteTimeout` after an
idempotent request (GET, HEAD, PUT, DELETE, OPTIONS, TRACE with a replayable body) is closed and the request is
sent once more on a fresh connection. That fresh connection gets the same bound: if it stays silent too, the
request fails, with no further attempt. A POST is never replayed and keeps waiting until the request timeout.
`Timeout.InfiniteTimeSpan` turns this off.

## HTTP/2 needs the BouncyCastle backend

HTTP/2 over TLS is selected only through ALPN (RFC 9113 section 3.2). Unity's `SslStream` (Mono with
UnityTls, in the Editor and in Mono and IL2CPP players on every platform) accepts the ALPN options but
never sends them and cannot report a negotiated protocol (mono#12880). With the default `TlsBackend.Auto`
(or `TlsBackend.SslStream`) every HTTPS connection therefore uses HTTP/1.1, and TurboHTTP reports no
ALPN and no HTTP/2 capability for it (`PlatformConfig.SupportsHttp2` is false without BouncyCastle).
`Auto` never switches to BouncyCastle by itself.

To use HTTP/2, add `TURBOHTTP_BOUNCYCASTLE` to Player Settings > Scripting Define Symbols and set
`UHttpClientOptions.TlsBackend = TlsBackend.BouncyCastle`; see [Transport](Modules/Transport.md) for its
certificate validation per platform.

## Known limitation: TLS server name for IP-address hosts

For `https://` requests to an IP address (for example `https://127.0.0.1/` or `https://[::1]/`),
RFC 6066 section 3 says the client sends no `server_name` (SNI) extension. The BouncyCastle
provider follows that. Unity's own `SslStream` (Mono with UnityTls, used in the Editor and in
players) sends the address as `server_name` anyway. It derives SNI from the TLS target host, and
the same target host is the name the server certificate is checked against (its iPAddress entries),
so TurboHTTP cannot leave it out without weakening certificate validation. Certificate checks still
apply in full; only the extra extension goes out.

Most servers ignore an IP address in SNI. If yours rejects it, connect through a DNS name, or define
`TURBOHTTP_BOUNCYCASTLE` and select `TlsBackend.BouncyCastle` for that client.

## Cleartext traffic

TurboHTTP opens raw sockets, so the operating system's cleartext rules (iOS App Transport
Security, the Android network security configuration) are never applied to its connections.
TurboHTTP applies its own policy instead, before it resolves or connects any address:

- **What counts as cleartext:** an `http://` request, a `ws://` WebSocket, and the leg to an
  `http://` proxy (CONNECT and `Proxy-Authorization` travel unencrypted even for an `https://` or
  `wss://` target).
- **The switch:** `UHttpClientOptions.AllowCleartext` for HTTP and
  `WebSocketConnectionOptions.AllowCleartext` for WebSocket (the `client.WebSocket(...)` extension
  copies the client's value). It defaults to `CleartextTrafficPolicy.DefaultAllowCleartext`:
  **`false` on iOS and Android players**, `true` in the Editor and on desktop.
- **Platform policy:** when the switch is `true`, an installed `CleartextTrafficPolicy.PlatformPolicy`
  must also permit the host. On Android players the TurboHTTP Unity module installs one that asks
  `NetworkSecurityPolicy.isCleartextTrafficPermitted(host)`, so `android:usesCleartextTraffic` or a
  network security config domain rule is still needed on Android 9+. On iOS no Info.plist key is
  read; `AllowCleartext = true` is the explicit opt-in, and ATS keys only affect `UnityWebRequest`
  and other OS stacks.
- **Refusal:** HTTP throws `UHttpException` with `UHttpErrorType.InvalidRequest`; WebSocket throws
  `WebSocketException` with `WebSocketError.CleartextNotPermitted`. No socket is opened.
- **Proxy credentials (CONNECT tunnels):** for `https://` and `wss://` targets through a proxy,
  Basic proxy credentials are sent only in answer to a 407 whose `Proxy-Authenticate` offers
  `Basic` (RFC 9110 section 11.7.1), and only over a proxy leg that `AllowCleartext` permits
  (RFC 7617 section 4). The HTTP transport also requires `ProxySettings.AllowPlaintextProxyAuth`.
- **Proxy credentials (HTTP forward proxying):** for `http://` targets the forwarded request goes
  out without `Proxy-Authorization`. When the proxy answers 407 with a `Proxy-Authenticate` that
  offers `Basic`, the HTTP transport resends the request once with Basic credentials, provided
  `ProxySettings.AllowPlaintextProxyAuth` is `true` (the request is cleartext, so it also needs
  `AllowCleartext`) and the request body can be sent again. Otherwise (no Basic offer, a stream
  body that cannot be replayed, or a second 407) the 407 response is returned to the caller.

```csharp
using TurboHTTP.WebSocket;

// Local development server over plain HTTP / ws:// on a device:
var client = new UHttpClient(new UHttpClientOptions { AllowCleartext = true });
var wsOptions = new WebSocketConnectionOptions { AllowCleartext = true };
```

Device behaviour (the Android JNI policy, iOS players) is pending physical-device validation (#99).

## Platform-Specific Configurations

### iOS (IL2CPP)

#### Background Networking (experimental, opt-in)
`options.UseBackgroundNetworking()` holds a `UIApplication` background task while each request runs
and queues replayable requests that iOS expires. It is **experimental and off by default**: the native
bridge is built in only when the iOS Player Settings define `TURBOHTTP_BACKGROUND_NETWORKING` (see
[Background networking opt-in](#background-networking-opt-in)). Without the define nothing native is
linked and requests keep foreground semantics. A request is expired only by iOS's expiration handler
(the app is in the background and out of time), never by a timer while the app is in the foreground.
Device validation is pending (#266).

tvOS is not a supported target: the iOS native plugins (background bridge, SecTrust verifier) are
not shipped to tvOS, where no managed code calls them (#176).

#### App Transport Security (ATS)
ATS does not apply to raw sockets, so `Info.plist` ATS keys do not affect TurboHTTP. Cleartext
`http://` and `ws://` are refused by TurboHTTP's own default on iOS players; set
`AllowCleartext = true` to opt in (see [Cleartext traffic](#cleartext-traffic)).

#### IPv6 and NAT64
App Store submission requires IPv6 support. TurboHTTP natively supports IPv6 via standard C# Sockets and resolves names with Happy Eyeballs (RFC 8305), so a hostname works on dual-stack and IPv6-only networks. On a NAT64/DNS64 network connect to hostnames, not IPv4 literals (`http://203.0.113.5/` has no synthesized IPv6 address). Behavior on a real NAT64 network is `needs-device-validation` (#99); it has not been verified from the Editor.

### Android (IL2CPP)

#### Background Networking (experimental, opt-in)
With the Android Player Settings define `TURBOHTTP_BACKGROUND_NETWORKING`, the build includes
`Plugins/Android/TurboHttpBackground.androidlib`, a Gradle library that declares its own
`androidx.work:work-runtime` dependency (no Gradle template edits; WorkManager's manifest entries are
merged only then). `options.UseBackgroundNetworking()` then holds an in-process guard per request.
When the app has been paused for `AndroidBackgroundWorkConfig.BackgroundBudgetMilliseconds`
(default 5000; Android has no `backgroundTimeRemaining`, so the budget is yours to choose) the
scopes expire: in-flight requests are cancelled, a replayable one (GET/HEAD, or a replayable body)
is queued and handed to WorkManager, and a non-replayable one fails as it would in the foreground.
The budget starts at `OnApplicationPause` and is timed off the player loop. Without the define the
library is left out of the build and requests keep foreground semantics. Device validation is
pending (#266).

#### Background networking opt-in
1. Add `TURBOHTTP_BACKGROUND_NETWORKING` to **Player Settings > Other Settings > Scripting Define
   Symbols** for each target (iOS, Android). Defines passed only through
   `BuildPlayerOptions.extraScriptingDefines` are not seen by the plugin gate; use Player Settings.
2. Call `options.UseBackgroundNetworking()` on the client options.

The API is experimental: behavior and types may change before it is promoted.

#### Cleartext traffic on Android
Android 9+ (API 28+) forbids cleartext by default. TurboHTTP refuses it unless
`AllowCleartext = true` **and** the app's policy permits the host: add
`android:usesCleartextTraffic="true"` to `<application>` in `AndroidManifest.xml`, or a network
security config domain rule (see [Cleartext traffic](#cleartext-traffic)).

#### Permissions
Ensure the internet permission is in `AndroidManifest.xml`:
```xml
<uses-permission android:name="android.permission.INTERNET" />
```

## IL2CPP & Code Stripping

TurboHTTP is AOT-safe, but aggressive code stripping (`Managed Stripping Level: High`) can sometimes remove necessary code.

### Link.xml
UnityLinker does not read `link.xml` files inside packages, so TurboHTTP ships an
`IUnityLinkerProcessor` (`TurboHttpLinkerProcessor`) that passes the package's own preserve rules
to every player build. Each module's rules apply only when that module's assembly is in the build.

On the **Mono** backend, `SslStream`'s first handshake initializes the runtime's configuration system,
which creates its host and the `system.net` configuration sections by reflection. Managed Stripping
Level Medium or High would strip their constructors and fail every `SslStream` (and `TlsBackend.Auto`)
request with an authentication error that looks like a certificate problem. The package's rules keep
them, so Mono players work at Low, Medium and High. If a Mono player still logs `Default constructor
not found for type System.Configuration.ExeConfigurationHost`, the package's linker processor did not
run for that build: copy the `#450` rules of the package's `Runtime/Transport/link.xml` into your
project's `link.xml`.

If you still encounter missing methods or types, add a `link.xml` to your project's `Assets` folder.
On Unity, `SslStream` and the other `System.Net.Security` types live in the `System` assembly:

```xml
<linker>
    <assembly fullname="TurboHTTP.Core" preserve="all"/>
    <assembly fullname="TurboHTTP.Transport" preserve="all"/>
    <assembly fullname="System">
        <type fullname="System.Net.Security.SslStream" preserve="all"/>
    </assembly>
    <!-- Preserve your own DTO namespaces -->
    <assembly fullname="MyGame.Network.DTOs" preserve="all"/>
</linker>
```

### Diagnostics
If issues persist:
1.  Enable **Development Build** and **Script Debugging**.
2.  Check Player Logs for `ExecutionEngineException` or `MissingMethodException`.
3.  Run the package's PlayMode tests (`TurboHTTP.Tests.Runtime`, `PlatformTests`) in a development player: they include the IL2CPP/AOT smoke checks (the `IL2CPPCompatibility` helper is internal since 1.0).

## Managed allocations per body read

Reading a streaming response body allocates little managed memory per read in optimized code. The
package's allocation tests measure it per read, with a budget for each runtime:

| Build | Counter | HTTP/1.1 read | HTTP/1.1 chunked read | HTTP/2 read | gzip read (4 KiB) |
| --- | --- | --- | --- | --- | --- |
| Editor (Mono, optimized code) | GC Allocated In Frame | 40 B | 32 B | 1 B | ~4.2 KB |
| Development player, Mono (Linux x86_64) | GC Allocated In Frame | 694 B | 1349 B | 394 B | ~4.3 KB |
| Development player, IL2CPP (Android arm64) | GC Allocated In Frame | 705 B | 1371 B | 411 B | ~4.1 KB |

- A development build compiles your scripts and the package without optimization. Every `async` method
  then allocates its state machine, even when it completes without waiting (72 B per call on Mono, about
  98 B on arm64 IL2CPP). That is most of the difference between the editor and the development
  players. A release build compiles with optimization and does not have it.
- The remaining per-read allocations are tracked for a release after 1.0: Mono in #434, IL2CPP in #416.
- On Unity 2021.3 IL2CPP, `GC.GetAllocatedBytesForCurrentThread` counts in 4 KiB steps and includes
  allocations made on other threads. Do not use it to measure small allocations; use the profiler's
  "GC Allocated In Frame" counter over a frame that runs only the code you measure, and repeat the
  measurement.
