# Troubleshooting Guide

Common issues and solutions for TurboHTTP.

## Common Issues

### Request Timeout

**Symptom:** Requests fail with `UHttpErrorType.Timeout`.

**Solutions:**
1.  **Increase Timeout:** `.WithTimeout(TimeSpan.FromSeconds(60))`
2.  **Network Check:** Verify device connectivity.
3.  **DNS Issues:** If DNS is slow, configure `TcpConnectionPool(dnsTimeout: ...)` with a higher value.
4.  **Mobile:** Mobile networks can be high latency; default timeout increases on mobile (45s), but you may need more.

### SSL/TLS Handshake Failures

**Symptom:** `UHttpErrorType.CertificateError` or "Handshake failed".

**Solutions:**
1.  **HTTPS vs HTTP:** Ensure you aren't trying to speak HTTPS to an HTTP port (or vice versa).
2.  **Date/Time:** Check if the device clock is correct.
3.  **Cert Validity:** Verify the server certificate is valid and not expired.
4.  **Intermediates:** Ensure the server sends the full certificate chain.
5.  **iOS/Android:** Check if the OS trusts the specific CA signing the cert.

### JSON Deserialization Errors

**Symptom:** Exception when calling `.AsJson<T>()`.

**Solutions:**
1.  **Public Properties:** Ideally use properties (`{ get; set; }`) for your DTOs.
2.  **Attributes:** Use `[Serializable]` or `[JsonPropertyName]` if keys don't match.
3.  **Structure:** Verify the JSON response matches your class structure.
4.  **IL2CPP:** Ensure your DTO class isn't being stripped (see [Platform Notes](PlatformNotes.md)).

## Platform-Specific

### iOS

*   **"Cleartext ... is not permitted"**: You are trying to use `http://`, `ws://` or an `http://` proxy. Use HTTPS/WSS, or set `AllowCleartext = true` (ATS keys in Info.plist do not apply to TurboHTTP's raw sockets; see PlatformNotes, "Cleartext traffic").
*   **Background Timeout**: iOS apps have very limited execution time in background (~30s). Requests may be cut off. Background networking is experimental and needs the `TURBOHTTP_BACKGROUND_NETWORKING` define (PlatformNotes).

### Android

*   **"Cleartext ... is not permitted"**: Set `AllowCleartext = true` and permit the host in AndroidManifest.xml (`usesCleartextTraffic`) or the network security config (see PlatformNotes, "Cleartext traffic").
*   **Permission Denied**: Missing `android.permission.INTERNET`.

## Tools

### HTTP Monitor Window
If the HTTP Monitor (Window -> TurboHTTP -> Monitor) is empty:
1.  Ensure **Enable HTTP Monitor** is checked in Preferences -> TurboHTTP.
2.  Ensure you have `include HTTP Monitor` defined in your build settings/defines if using custom defines.

### Logging
Enable detailed logging to debug issues:

```csharp
using TurboHTTP.Observability;

options.Interceptors.Insert(0, new LoggingInterceptor(UnityEngine.Debug.Log, LoggingInterceptor.LogLevel.Detailed));
```

### Library warnings on devices
Failures TurboHTTP swallows on purpose (background cache stores, dispose-time errors) appear as
`[TurboHTTP]` warnings in the Unity console and device logs. Route them elsewhere with
`TurboHttpDiagnostics.SetSink(...)` (see Modules/Core.md, "Diagnostics").
