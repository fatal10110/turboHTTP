# Troubleshooting Guide

Common issues and solutions for TurboHTTP.

## Request Timeout

**Symptom:** Requests fail with `UHttpErrorType.Timeout`

**Solutions:**
1. Increase timeout: `.WithTimeout(TimeSpan.FromSeconds(60))`
2. Check network connectivity
3. Verify server is responding
4. On mobile, use longer timeouts (60s+)
5. If DNS resolution is slow on the target network, increase DNS timeout via `TcpConnectionPool(dnsTimeout: ...)`.

## SSL/TLS Errors

**Symptom:** "An SSL error has occurred" or `UHttpErrorType.CertificateError`

**Solutions:**
1. Ensure using HTTPS (not HTTP)
2. Verify certificate is valid
3. On iOS, check App Transport Security (ATS) settings
4. Update Unity to latest version for latest TLS support
5. If using self-signed certificates, ensure you have a custom validation callback or install the certificate on the device (not recommended for production)

## JSON Deserialization Fails

**Symptom:** Exception when calling `.AsJson<T>()`

**Solutions:**
1. Ensure class has public properties
2. Add `[Serializable]` attribute to class
3. Check JSON structure matches class
4. Use `TryAsJson` for safer parsing (if available) or `try-catch` blocks
5. Check for IL2CPP stripping issues (see Platform Notes)

## Platform-Specific Issues

### iOS

**Issue:** "Cleartext not permitted"
- Use HTTPS instead of HTTP
- Or configure ATS exceptions in Info.plist

**Issue:** Background requests timeout
- Background execution limited to 30s on iOS
- Use shorter timeouts for background requests

### Android

**Issue:** "java.net.UnknownServiceException: CLEARTEXT"
- Add `android:usesCleartextTraffic="true"` to AndroidManifest.xml
- Or use HTTPS

**Issue:** Permission denied
- Add `<uses-permission android:name="android.permission.INTERNET" />` to manifest

## Memory Issues

**Symptom:** High memory usage or GC spikes

**Solutions:**
1. Dispose clients: `client.Dispose()`
2. Enable memory pooling (automatic in v1.0)
3. Limit concurrent requests with `ConcurrencyMiddleware` (if available)
4. Clear cache periodically

## Unity Editor Issues

**Issue:** HTTP Monitor not showing requests

**Solutions:**
1. Check Window → TurboHTTP → HTTP Monitor
2. Verify MonitorMiddleware is in pipeline (if manually configured)
3. Check Preferences → TurboHTTP → "Enable HTTP Monitor"

## IL2CPP Build Issues

**Issue:** NotSupportedException or missing methods

**Solutions:**
1. Run `IL2CPPCompatibility.ValidateCompatibility()` (if available)
2. Ensure using System.Text.Json (not Newtonsoft.Json if not configured)
3. Add `link.xml` if needed for JSON types to prevent stripping
4. If forcing `TlsBackend.SslStream` for HTTP/2, preserve SslStream ALPN types in `link.xml` (`SslStream`, `SslClientAuthenticationOptions`, `SslApplicationProtocol`)
5. If ALPN remains unreliable on device, switch to `TlsBackend.BouncyCastle`

## Getting Help

1. Check [API Reference](APIReference.md)
2. Check [Platform Notes](PlatformNotes.md)
3. Review [Samples](../Samples~/)
4. Contact support: support@yourcompany.com
