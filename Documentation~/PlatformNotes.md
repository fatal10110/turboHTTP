# Platform Support and Troubleshooting

TurboHTTP is designed to run reliably across major Unity platforms, supporting both Mono and IL2CPP scripting backends. This document outlines supported configurations, default behaviors, and troubleshooting steps.

## Supported Platforms Matrix

| Platform | Scripting Backend | Status | Notes |
|----------|-------------------|--------|-------|
| **Windows** | Mono / IL2CPP | ✅ Supported | Full HTTP/2 and TLS 1.3 support. |
| **macOS** | Mono / IL2CPP | ✅ Supported | Full HTTP/2 and TLS 1.3 support. |
| **Linux** | Mono / IL2CPP | ✅ Supported | Verified on Ubuntu 20.04+. |
| **iOS** | IL2CPP | ✅ Supported | Requires AOT-safe code (verified via `IL2CPPCompatibility`). |
| **Android** | IL2CPP | ✅ Supported | ARM64/ARMv7. Restrictions on cleartext traffic. |
| **WebGL** | IL2CPP | ❌ Not Supported | Requires custom browser-fetch transport adapter (planned). |
| **Console** | IL2CPP | ⚠️ Experimental | Core logic compatible; socket implementation varies by platform SDK. |

## Platform Configuration Defaults

TurboHTTP automatically detects the runtime platform and applies optimized defaults for:
- `UHttpClientOptions.DefaultTimeout`
- `RawSocketTransport` default per-host connection concurrency

| Feature | Desktop / Editor | Mobile (iOS/Android) |
|---------|------------------|----------------------|
| **Request Timeout** | 30 seconds | 45 seconds |
| **Max Concurrency** | 16 requests | 8 requests |
| **TLS Provider (Auto)** | SslStream on Windows/macOS Editor/Standalone; Linux prefers BouncyCastle with SslStream fallback | SslStream when ALPN APIs are available; BouncyCastle fallback when they are not |

### Overriding Defaults
You can override timeout globally via `UHttpClientOptions` or per-request:

```csharp
var options = new UHttpClientOptions
{
    // Force a specific timeout regardless of platform
    DefaultTimeout = TimeSpan.FromSeconds(60)
};
```

To override connection concurrency defaults, provide a custom transport/pool:

```csharp
var pool = new TcpConnectionPool(maxConnectionsPerHost: 4);
var transport = new RawSocketTransport(pool);
var client = new UHttpClient(new UHttpClientOptions
{
    Transport = transport,
    DisposeTransport = true
});
```

## Platform Specifics

### iOS (IL2CPP)

#### App Transport Security (ATS)
iOS requires all HTTP connections to use HTTPS by default. To allow HTTP (e.g., for local development), add an exception to your `Info.plist`:

```xml
<key>NSAppTransportSecurity</key>
<dict>
    <key>NSAllowsArbitraryLoads</key>
    <true/>
</dict>
```

#### Background Execution
iOS suspends apps quickly when entering the background. TurboHTTP does not currently support `NSURLSession` background transfers. Ensure requests are cancellable or complete quickly before suspension.

#### IPv6 Support
App Store submissions require IPv6 support. TurboHTTP uses C#'s `Socket` which supports IPv6 automatically if the network provides it. Test in an IPv6-only environment (e.g., NAT64) before release.

### Android (IL2CPP)

#### Cleartext Traffic
Android 9 (API 28+) blocks cleartext (HTTP) traffic by default. To allow it, add `android:usesCleartextTraffic="true"` to `AndroidManifest.xml`:

```xml
<application ... android:usesCleartextTraffic="true"> ... </application>
```

#### Permissions
Ensure `INTERNET` permission is requested in `AndroidManifest.xml`:

```xml
<uses-permission android:name="android.permission.INTERNET" />
```

### WebGL
**Status:** Not currently supported in v1.0. 
The current socket-based implementation is incompatible with browser sandboxes. A generic `Fetch` adapter is planned for future releases.

## IL2CPP & AOT Compatibility

TurboHTTP is designed to be AOT-safe. However, aggressive code stripping can remove required code paths.

### Diagnostics
If you encounter `ExecutionEngineException` or `MissingMethodException` on IL2CPP builds:
1. Enable **Development Build** and **Script Debugging**.
2. Check the Player Log for the specific missing type/method.
3. Call `IL2CPPCompatibility.Validate(out string report)` at startup to verify the environment.

### Code Stripping & Link.xml
If you use **Managed Stripping Level: High**, include a `link.xml` file to preserve TurboHTTP assemblies and types used in JSON serialization:

```xml
<linker>
    <assembly fullname="TurboHTTP.Core" preserve="all"/>
    <assembly fullname="TurboHTTP.Transport" preserve="all"/>
    <!-- Preserve your own DTOs if using reflection-based JSON -->
    <assembly fullname="MyGame.Network.DTOs" preserve="all"/>
</linker>
```

If you force `TlsBackend.SslStream` and need HTTP/2 ALPN on IL2CPP, also preserve SslStream ALPN types:

```xml
<linker>
    <assembly fullname="System.Net.Security">
        <type fullname="System.Net.Security.SslStream" preserve="all"/>
        <type fullname="System.Net.Security.SslClientAuthenticationOptions" preserve="all"/>
        <type fullname="System.Net.Security.SslApplicationProtocol" preserve="all"/>
    </assembly>
</linker>
```

If ALPN still fails on a target device, prefer `TlsBackend.BouncyCastle` for guaranteed ALPN support.

## Troubleshooting

### TLS Handshake Failed
**Symptom:** `TlsException: Handshake failed` or `AuthenticationException`.
**Fix:**
- Verify server supports TLS 1.2 or 1.3.
- Check device clock time.
- Use certificates trusted by the platform trust store (custom validation callbacks are not exposed in the current public API).

### "Protocol Error" (HTTP/2)
**Symptom:** Connection closes unexpectedly with HTTP/2 protocol error.
**Fix:** Force HTTP/1.1 if the network/proxy is unstable with HTTP/2.

### Slow Connect on Dual-Stack Networks
**Symptom:** New connections pause before succeeding on some mobile networks.
**Cause:** TurboHTTP currently tries IPv6 first, then IPv4 (sequential fallback).
**Fix:** Increase timeout budgets and validate network DNS/IPv6 quality. Full Happy Eyeballs parallel racing is planned for a future release.

### System.Text.Json Serialization Errors
**Symptom:** `JsonException` or missing property errors on IL2CPP.
**Fix:** Ensure your data classes are public and verify `link.xml` preserves the assembly containing them. Prefer source-generated deserializers if available.
