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

*   **"Cleartext not permitted"**: You are trying to use HTTP. Use HTTPS or configure ATS in Info.plist.
*   **Background Timeout**: iOS apps have very limited execution time in background (~30s). Requests may be cut off.

### Android

*   **"java.net.UnknownServiceException: CLEARTEXT"**: You need to enable cleartext traffic in AndroidManifest.xml if using HTTP.
*   **Permission Denied**: Missing `android.permission.INTERNET`.

## Tools

### HTTP Monitor Window
If the HTTP Monitor (Window -> TurboHTTP -> Monitor) is empty:
1.  Ensure **Enable HTTP Monitor** is checked in Preferences -> TurboHTTP.
2.  Ensure you have `include HTTP Monitor` defined in your build settings/defines if using custom defines.

### Logging
Enable detailed logging to debug issues:

```csharp
using TurboHTTP.Middleware;

options.Middlewares.Add(new LoggingMiddleware(LoggingMiddleware.LogLevel.Detailed));
```
