# Phase 9: Platform Compatibility

**Milestone:** M2 (v0.5 "hardening gate")
**Dependencies:** Phase 8 (Documentation & Samples)
**Estimated Complexity:** Medium
**Critical:** Yes - Platform support validation

## Overview

Validate TurboHTTP works correctly on all target platforms: Editor, Standalone (Windows/Mac/Linux), iOS, and Android. Since TurboHTTP uses raw TCP sockets and `SslStream` for TLS (rather than UnityWebRequest), this phase is critical — the transport layer must be validated against platform-specific socket and TLS behavior, especially under IL2CPP. Key areas: `SslStream` with ALPN for HTTP/2 negotiation, certificate validation, socket connection pooling, and platform threading models.

Detailed sub-phase breakdown: [Phase 9 Implementation Plan - Overview](phase9/overview.md)

## Goals

1. Test on Unity Editor (Windows, Mac, Linux)
2. Test on Standalone builds (Windows, Mac, Linux)
3. Test on iOS (device and simulator)
4. Test on Android (multiple devices)
5. Validate IL2CPP builds work correctly (especially `SslStream` + ALPN)
6. Validate HTTP/2 via ALPN on all platforms
7. Document platform-specific limitations
8. Create platform-specific test builds
9. Fix any platform-specific bugs

## Tasks

### Task 9.1: Platform Detection Utility

**File:** `Runtime/Core/PlatformInfo.cs`

```csharp
using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Platform information and detection utilities.
    /// </summary>
    public static class PlatformInfo
    {
        /// <summary>
        /// Current runtime platform.
        /// </summary>
        public static RuntimePlatform Platform => Application.platform;

        /// <summary>
        /// Check if running in Unity Editor.
        /// </summary>
        public static bool IsEditor =>
            Platform == RuntimePlatform.WindowsEditor ||
            Platform == RuntimePlatform.OSXEditor ||
            Platform == RuntimePlatform.LinuxEditor;

        /// <summary>
        /// Check if running on mobile platform.
        /// </summary>
        public static bool IsMobile =>
            Platform == RuntimePlatform.IPhonePlayer ||
            Platform == RuntimePlatform.Android;

        /// <summary>
        /// Check if running on iOS.
        /// </summary>
        public static bool IsIOS => Platform == RuntimePlatform.IPhonePlayer;

        /// <summary>
        /// Check if running on Android.
        /// </summary>
        public static bool IsAndroid => Platform == RuntimePlatform.Android;

        /// <summary>
        /// Check if running on desktop.
        /// </summary>
        public static bool IsDesktop =>
            Platform == RuntimePlatform.WindowsPlayer ||
            Platform == RuntimePlatform.OSXPlayer ||
            Platform == RuntimePlatform.LinuxPlayer;

        /// <summary>
        /// Check if running with IL2CPP scripting backend.
        /// </summary>
        public static bool IsIL2CPP
        {
            get
            {
#if ENABLE_IL2CPP
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Get a descriptive string about the current platform.
        /// </summary>
        public static string GetPlatformDescription()
        {
            var backend = IsIL2CPP ? "IL2CPP" : "Mono";
            return $"{Platform} ({backend}) Unity {Application.unityVersion}";
        }
    }
}
```

### Task 9.2: Platform-Specific Configuration

**File:** `Runtime/Core/PlatformConfig.cs`

```csharp
using System;
using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Platform-specific configuration and workarounds.
    /// </summary>
    public static class PlatformConfig
    {
        /// <summary>
        /// Get recommended timeout for current platform.
        /// Mobile platforms may need longer timeouts.
        /// </summary>
        public static TimeSpan GetRecommendedTimeout()
        {
            if (PlatformInfo.IsMobile)
            {
                return TimeSpan.FromSeconds(60); // Mobile networks can be slow
            }
            return TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Get recommended max concurrent connections for current platform.
        /// </summary>
        public static int GetRecommendedMaxConcurrent()
        {
            if (PlatformInfo.IsMobile)
            {
                return 4; // Limit on mobile to conserve battery
            }
            return 6;
        }

        /// <summary>
        /// Check if TLS 1.2+ is available via SslStream on current platform.
        /// </summary>
        public static bool IsTLS12Available()
        {
            // SslStream supports TLS 1.2 on iOS 9+, Android 5+, all desktop platforms
            return true;
        }

        /// <summary>
        /// Check if SslStream certificate validation callback can be customized.
        /// </summary>
        public static bool CanCustomizeCertificateValidation()
        {
            // SslStream's RemoteCertificateValidationCallback works on most platforms
            // iOS restricts some certificate validation customization at OS level
            return !PlatformInfo.IsIOS;
        }

        /// <summary>
        /// Log platform-specific information.
        /// </summary>
        public static void LogPlatformInfo()
        {
            Debug.Log($"[TurboHTTP Platform] {PlatformInfo.GetPlatformDescription()}");
            Debug.Log($"[TurboHTTP Platform] Recommended Timeout: {GetRecommendedTimeout().TotalSeconds}s");
            Debug.Log($"[TurboHTTP Platform] Recommended Max Concurrent: {GetRecommendedMaxConcurrent()}");
            Debug.Log($"[TurboHTTP Platform] TLS 1.2+: {IsTLS12Available()}");
            Debug.Log($"[TurboHTTP Platform] Custom Cert Validation: {CanCustomizeCertificateValidation()}");
        }
    }
}
```

### Task 9.3: IL2CPP Compatibility Check

**File:** `Runtime/Core/IL2CPPCompatibility.cs`

```csharp
using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace TurboHTTP.Core
{
    /// <summary>
    /// IL2CPP compatibility validation.
    /// </summary>
    public static class IL2CPPCompatibility
    {
        /// <summary>
        /// Check if all TurboHTTP features are compatible with IL2CPP.
        /// </summary>
        public static bool ValidateCompatibility()
        {
            var allPassed = true;

            allPassed &= CheckReflection();
            allPassed &= CheckSerialization();
            allPassed &= CheckAsync();

            if (allPassed)
            {
                Debug.Log("[TurboHTTP IL2CPP] All compatibility checks passed");
            }
            else
            {
                Debug.LogError("[TurboHTTP IL2CPP] Some compatibility checks failed");
            }

            return allPassed;
        }

        private static bool CheckReflection()
        {
            try
            {
                // Test basic reflection (used for JSON serialization)
                var testObj = new { name = "test", value = 123 };
                var type = testObj.GetType();
                var properties = type.GetProperties();

                Debug.Log($"[IL2CPP Check] Reflection: PASS ({properties.Length} properties)");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IL2CPP Check] Reflection: FAIL - {ex.Message}");
                return false;
            }
        }

        private static bool CheckSerialization()
        {
            try
            {
                // Test JSON serialization with System.Text.Json
                var testObj = new TestClass { Name = "test", Value = 123 };
                var json = System.Text.Json.JsonSerializer.Serialize(testObj);
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<TestClass>(json);

                if (deserialized.Name == "test" && deserialized.Value == 123)
                {
                    Debug.Log("[IL2CPP Check] Serialization: PASS");
                    return true;
                }

                Debug.LogError("[IL2CPP Check] Serialization: FAIL - Data mismatch");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IL2CPP Check] Serialization: FAIL - {ex.Message}");
                return false;
            }
        }

        private static bool CheckAsync()
        {
            try
            {
                // Test async/await
                var task = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(10);
                    return 42;
                });

                task.Wait();

                if (task.Result == 42)
                {
                    Debug.Log("[IL2CPP Check] Async/Await: PASS");
                    return true;
                }

                Debug.LogError("[IL2CPP Check] Async/Await: FAIL - Incorrect result");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[IL2CPP Check] Async/Await: FAIL - {ex.Message}");
                return false;
            }
        }

        [Serializable]
        private class TestClass
        {
            public string Name { get; set; }
            public int Value { get; set; }
        }
    }
}
```

### Task 9.4: Platform Test Suite

**File:** `Tests/Runtime/Platform/PlatformTests.cs`

```csharp
using NUnit.Framework;
using System.Threading.Tasks;
using TurboHTTP.Core;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.Platform
{
    /// <summary>
    /// Platform-specific tests to validate compatibility.
    /// </summary>
    public class PlatformTests
    {
        [Test]
        public void PlatformDetection_IsCorrect()
        {
            Debug.Log($"Platform: {PlatformInfo.Platform}");
            Debug.Log($"Is Editor: {PlatformInfo.IsEditor}");
            Debug.Log($"Is Mobile: {PlatformInfo.IsMobile}");
            Debug.Log($"Is IL2CPP: {PlatformInfo.IsIL2CPP}");

            Assert.IsNotNull(PlatformInfo.Platform);
        }

        [Test]
        public void PlatformConfig_ReturnsValidValues()
        {
            var timeout = PlatformConfig.GetRecommendedTimeout();
            var maxConcurrent = PlatformConfig.GetRecommendedMaxConcurrent();

            Assert.Greater(timeout.TotalSeconds, 0);
            Assert.Greater(maxConcurrent, 0);

            PlatformConfig.LogPlatformInfo();
        }

        [Test]
        public void IL2CPP_CompatibilityCheck()
        {
            var compatible = IL2CPPCompatibility.ValidateCompatibility();
            Assert.IsTrue(compatible, "IL2CPP compatibility check failed");
        }

        [UnityTest]
        public IEnumerator HttpRequest_WorksOnCurrentPlatform()
        {
            var client = new UHttpClient();

            var task = client.Get("https://httpbin.org/get").SendAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.IsFaulted)
            {
                Debug.LogError($"Request failed: {task.Exception}");
                Assert.Fail($"HTTP request failed on {PlatformInfo.Platform}");
            }

            Assert.IsTrue(task.Result.IsSuccessStatusCode);
            Debug.Log($"HTTP request successful on {PlatformInfo.Platform}");
        }

        [UnityTest]
        public IEnumerator JsonSerialization_WorksOnCurrentPlatform()
        {
            var client = new UHttpClient();

            var data = new { name = "test", value = 123 };
            var task = client.PostJsonAsync<object, dynamic>(
                "https://httpbin.org/post",
                data
            );

            yield return new WaitUntil(() => task.IsCompleted);

            if (task.IsFaulted)
            {
                Debug.LogError($"JSON request failed: {task.Exception}");
                Assert.Fail($"JSON serialization failed on {PlatformInfo.Platform}");
            }

            Assert.IsNotNull(task.Result);
            Debug.Log($"JSON serialization successful on {PlatformInfo.Platform}");
        }

        [UnityTest]
        public IEnumerator SSL_WorksOnCurrentPlatform()
        {
            var client = new UHttpClient();

            // Test HTTPS connection
            var task = client.Get("https://www.google.com").SendAsync();
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.IsFaulted)
            {
                Debug.LogError($"SSL request failed: {task.Exception}");
                Assert.Fail($"SSL/TLS failed on {PlatformInfo.Platform}");
            }

            Assert.IsTrue(task.Result.IsSuccessStatusCode);
            Debug.Log($"SSL/TLS successful on {PlatformInfo.Platform}");
        }
    }
}
```

### Task 9.5: Platform-Specific Documentation

**File:** `Documentation~/PlatformNotes.md`

```markdown
# Platform Compatibility Notes

## Supported Platforms

TurboHTTP v1.0 officially supports:

- **Unity Editor:** Windows, macOS, Linux
- **Standalone:** Windows, macOS, Linux
- **Mobile:** iOS 12+, Android 5.0+ (API 21+)

## Platform-Specific Notes

### iOS

**Supported:**
- HTTPS requests via `SslStream` + raw TCP sockets
- HTTP/1.1 with connection keep-alive
- HTTP/2 via ALPN negotiation (requires `SslStream` ALPN support under IL2CPP — validate early)
- TLS 1.2+ via `SslStream`
- Background downloads (with limitations)

**Limitations:**
- `SslStream` certificate validation callback behavior may differ under IL2CPP — test thoroughly
- App Transport Security (ATS) requires HTTPS by default
- Background requests limited to 30 seconds in background mode
- ALPN support in `SslStream` under IL2CPP is the critical risk for HTTP/2 on iOS

**Recommendations:**
- Use HTTPS for all requests
- Configure ATS exceptions in Info.plist if cleartext HTTP is needed
- Set longer timeouts (60s) for mobile networks
- **Run the SslStream + ALPN IL2CPP spike (risk spike #2) on iOS before full implementation**

### Android

**Supported:**
- HTTPS requests via `SslStream` + raw TCP sockets
- HTTP/1.1 with connection keep-alive
- HTTP/2 via ALPN negotiation
- TLS 1.2+ (Android 5.0+) via `SslStream`
- Custom certificate validation via `RemoteCertificateValidationCallback`

**Limitations:**
- Cleartext (HTTP) traffic disabled by default on Android 9+
- Network permissions required in manifest
- `SslStream` ALPN behavior should be verified on older Android versions (5.0-7.0)

**Recommendations:**
- Add `android.permission.INTERNET` to AndroidManifest.xml
- For cleartext HTTP, configure `android:usesCleartextTraffic="true"` in manifest
- Test on multiple Android versions (5.0 through 14+)
- **Run the SslStream + ALPN IL2CPP spike on Android before full implementation**

### IL2CPP

**Compatibility:**
- Raw TCP sockets (`System.Net.Sockets.Socket`) work correctly under IL2CPP
- `SslStream` basic TLS works under IL2CPP
- **ALPN negotiation in `SslStream` must be validated** — this is the critical unknown for HTTP/2
- `System.Text.Json` works correctly under IL2CPP
- Async/await fully supported
- Reflection used minimally (only for JSON serialization)

**Critical Validation:**
- `SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions)` with `ApplicationProtocols` set — does ALPN work?
- `SslStream.NegotiatedApplicationProtocol` — does it return the correct value?
- If ALPN fails under IL2CPP: HTTP/2 will fall back to HTTP/1.1 automatically (no breakage, but no h2)
- If `SslStream` itself fails under IL2CPP: consider BouncyCastle as a fallback TLS provider

**Tested Configurations:**
- iOS + IL2CPP: `SslStream` basic ✓, ALPN ⚠️ (must validate)
- Android + IL2CPP: `SslStream` basic ✓, ALPN ⚠️ (must validate)
- Standalone + IL2CPP ✓

### WebGL

**Status:** NOT SUPPORTED in v1.0

WebGL support is planned for v1.1 (see Phase 16 roadmap).

**Reason:**
- Raw TCP sockets are not available in WebGL (browser sandbox)
- Requires a separate transport using browser `fetch()` API via `.jslib` interop (same approach as BestHTTP)
- CORS restrictions apply (browser-enforced)
- Different threading model (single-threaded)
- HTTP/2 is handled transparently by the browser — no client-side protocol choice

## Performance Characteristics

### Editor
- Fastest performance
- Full debugging support
- HTTP Monitor window available

### Standalone
- Production performance
- No Unity Editor overhead
- Recommended for performance testing

### Mobile
- Slower network speeds
- Battery constraints
- Use recommended mobile settings:
  - Timeout: 60s
  - Max concurrent: 4
  - Enable caching

## Testing Checklist

Before releasing on a platform, verify:

- [ ] Raw TCP socket connections work
- [ ] `SslStream` TLS handshake completes (HTTPS)
- [ ] `SslStream` ALPN negotiation returns "h2" on HTTP/2-capable servers
- [ ] HTTP/1.1 requests work (GET, POST)
- [ ] HTTP/2 requests work (multiplexed)
- [ ] Connection pooling and keep-alive work
- [ ] JSON serialization/deserialization works
- [ ] File downloads work
- [ ] Texture/AudioClip loading works (if applicable)
- [ ] Error handling works correctly (socket errors, TLS errors, timeouts)
- [ ] Timeline events are captured
- [ ] No crashes or memory leaks
- [ ] Performance is acceptable

## Troubleshooting

### iOS: "An SSL error has occurred" / `AuthenticationException`
- Ensure using HTTPS
- Check ATS configuration in Info.plist
- Verify certificate is valid
- If `SslStream.AuthenticateAsClientAsync` fails under IL2CPP, check if `SslClientAuthenticationOptions` is fully supported
- Try removing ALPN protocols to test if basic TLS works without h2 negotiation

### Android: "Cleartext HTTP traffic not permitted"
- Add `android:usesCleartextTraffic="true"` to manifest
- Or use HTTPS instead

### IL2CPP: "NotSupportedException"
- Run IL2CPPCompatibility.ValidateCompatibility()
- Check for use of unsupported reflection
- Ensure all types used in JSON are IL2CPP compatible

### All Platforms: Timeout errors
- Increase timeout value
- Check network connectivity
- Verify server is responsive

## Platform-Specific Configuration Example

```csharp
var options = new UHttpClientOptions();

// Configure based on platform
if (PlatformInfo.IsMobile)
{
    options.DefaultTimeout = TimeSpan.FromSeconds(60);
    // Enable caching on mobile
    options.Middlewares.Add(new CacheMiddleware());
}
else
{
    options.DefaultTimeout = TimeSpan.FromSeconds(30);
}

// Limit concurrent requests on mobile
var maxConcurrent = PlatformConfig.GetRecommendedMaxConcurrent();
options.Middlewares.Add(new ConcurrencyMiddleware(maxConcurrent));

var client = new UHttpClient(options);
```
```

## Validation Criteria

### Success Criteria

- [ ] Platform detection works correctly
- [ ] IL2CPP compatibility validated
- [ ] Builds successfully on all platforms (Editor, Standalone, iOS, Android)
- [ ] All tests pass on all platforms
- [ ] Platform-specific configurations work
- [ ] Documentation complete for all platforms
- [ ] Known limitations documented

### Platform Test Matrix

| Platform | Scripting Backend | Test Status |
|----------|-------------------|-------------|
| Windows Editor | Mono | ✓ |
| Mac Editor | Mono | ✓ |
| Windows Standalone | Mono | ✓ |
| Windows Standalone | IL2CPP | ✓ |
| Mac Standalone | IL2CPP | ✓ |
| Linux Standalone | IL2CPP | ✓ |
| iOS Device | IL2CPP | ✓ |
| iOS Simulator | IL2CPP | ✓ |
| Android | IL2CPP | ✓ |
| Android | Mono | ✓ |

## Next Steps

Once Phase 9 is complete and validated:

1. Move to [Phase 10: Advanced Middleware](phase-10-advanced-middleware.md)
2. Implement cache middleware with ETag support
3. Implement rate limiting middleware
4. Begin M3 feature-complete phase

## Notes

- Test on real devices, not just simulators
- Test on multiple Android devices (different manufacturers)
- Test on both old and new OS versions
- Mobile networks behave differently than WiFi
- IL2CPP builds take longer but are production-ready
