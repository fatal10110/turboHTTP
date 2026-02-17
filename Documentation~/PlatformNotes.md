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
| **WebGL** | IL2CPP | ❌ Not Supported | Browser sandbox restrictions. Future implementation planned. |
| **Console** | IL2CPP | ⚠️ Experimental | Socket implementation varies by platform SDK. |

## Platform Defaults

TurboHTTP automatically allows the runtime platform to determine optimal defaults.

| Feature | Desktop / Editor | Mobile (iOS/Android) |
| :--- | :--- | :--- |
| **Request Timeout** | 30 seconds | 45 seconds |
| **Max Concurrency** | 16 requests | 8 requests |
| **TLS Provider** | SslStream | SslStream (if ALPN supported) or BouncyCastle fallback |

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

## Platform-Specific Configurations

### iOS (IL2CPP)

#### App Transport Security (ATS)
iOS blocks cleartext HTTP by default. To allow HTTP (e.g., for local dev), add to `Info.plist`:
```xml
<key>NSAppTransportSecurity</key>
<dict>
    <key>NSAllowsArbitraryLoads</key>
    <true/>
</dict>
```

#### IPv6
App Store submission requires IPv6 support. TurboHTTP natively supports IPv6 via standard C# Sockets.

### Android (IL2CPP)

#### Cleartext Traffic
Android 9+ (API 28+) blocks cleartext traffic. To allow it, add `android:usesCleartextTraffic="true"` to `<application>` in `AndroidManifest.xml`.

#### Permissions
Ensure the internet permission is in `AndroidManifest.xml`:
```xml
<uses-permission android:name="android.permission.INTERNET" />
```

## IL2CPP & Code Stripping

TurboHTTP is AOT-safe, but aggressive code stripping (`Managed Stripping Level: High`) can sometimes remove necessary code.

### Link.xml
If you encounter missing methods or types, add a `link.xml` to your project's `Assets` folder:

```xml
<linker>
    <assembly fullname="TurboHTTP.Core" preserve="all"/>
    <assembly fullname="TurboHTTP.Transport" preserve="all"/>
    <assembly fullname="System.Net.Security" preserve="all"/>
    <!-- Preserve your own DTO namespaces -->
    <assembly fullname="MyGame.Network.DTOs" preserve="all"/>
</linker>
```

### Diagnostics
If issues persist:
1.  Enable **Development Build** and **Script Debugging**.
2.  Check Player Logs for `ExecutionEngineException` or `MissingMethodException`.
3.  Run `IL2CPPCompatibility.Validate(out string report)` to generated a compatibility report.
