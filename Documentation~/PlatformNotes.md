# Platform Support & Compatibility

TurboHTTP is designed to run reliably across major Unity platforms, supporting both Mono and IL2CPP scripting backends.

## Supported Platforms Matrix

| Platform | Scripting Backend | Status | Notes |
| :--- | :--- | :--- | :--- |
| **Windows** | Mono / IL2CPP | ✅ Supported | Full HTTP/2 and TLS 1.3 support. |
| **macOS** | Mono / IL2CPP | ✅ Supported | Full HTTP/2 and TLS 1.3 support. |
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
| **TLS Provider** | SslStream | SslStream (Auto never falls back to BouncyCastle; select `TlsBackend.BouncyCastle` explicitly to use it; on devices it rejects every server certificate until the native verifiers land) |

### Overriding Defaults

```csharp
var options = new UHttpClientOptions
{
    // Force a global timeout
    DefaultTimeout = TimeSpan.FromSeconds(60)
};
```

To override concurrency, configure the Transport directly:

```csharp
var pool = new TcpConnectionPool(maxConnectionsPerHost: 4);
var transport = new RawSocketTransport(pool);
```

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
- **Proxy credentials (HTTP forward proxying):** for `http://` targets the HTTP transport still
  sends Basic `Proxy-Authorization` preemptively on the forwarded request when
  `ProxySettings.AllowPlaintextProxyAuth` is `true` (the request is cleartext, so it also needs
  `AllowCleartext`). Challenge-driven credentials for this path are tracked in #236.

```csharp
// Local development server over plain HTTP / ws:// on a device:
var client = new UHttpClient(new UHttpClientOptions { AllowCleartext = true });
var wsOptions = new WebSocketConnectionOptions { AllowCleartext = true };
```

Device behaviour (the Android JNI policy, iOS players) is pending physical-device validation (#99).

## Platform-Specific Configurations

### iOS (IL2CPP)

#### Background Networking
TurboHTTP natively supports `UIApplication.beginBackgroundTask` to allow critical requests to finish when the app enters the background. This is handled automatically by the transport layer.

#### App Transport Security (ATS)
ATS does not apply to raw sockets, so `Info.plist` ATS keys do not affect TurboHTTP. Cleartext
`http://` and `ws://` are refused by TurboHTTP's own default on iOS players; set
`AllowCleartext = true` to opt in (see [Cleartext traffic](#cleartext-traffic)).

#### IPv6
App Store submission requires IPv6 support. TurboHTTP natively supports IPv6 via standard C# Sockets.

### Android (IL2CPP)

#### Background Networking
TurboHTTP provides a native Android JNI bridge to ensure network connections can complete cleanly when the app is backgrounded, mitigating OS-level socket closures during transitions.

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
3.  Run `IL2CPPCompatibility.Validate(out string report)` to generated a compatibility report.
