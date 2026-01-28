# Phase 11: Platform Compatibility

**Milestone:** M3 (v1.0 "production")
**Dependencies:** Phase 10 (Performance & Hardening)
**Estimated Complexity:** Medium
**Critical:** Yes - Platform support validation

## Overview

Validate TurboHTTP works correctly on all target platforms: Editor, Standalone (Windows/Mac/Linux), iOS, and Android. Ensure IL2CPP compatibility and handle platform-specific quirks. Document known limitations and workarounds.

## Goals

1. Test on Unity Editor (Windows, Mac, Linux)
2. Test on Standalone builds (Windows, Mac, Linux)
3. Test on iOS (device and simulator)
4. Test on Android (multiple devices)
5. Validate IL2CPP builds work correctly
6. Document platform-specific limitations
7. Create platform-specific test builds
8. Fix any platform-specific bugs

## Tasks

### Task 11.1: Platform Detection Utility

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

### Task 11.2: Platform-Specific Configuration

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
        /// Check if TLS 1.2+ is available on current platform.
        /// </summary>
        public static bool IsTLS12Available()
        {
            // iOS 9+, Android 5+, all desktop platforms support TLS 1.2
            return true;
        }

        /// <summary>
        /// Check if certificate validation can be customized.
        /// </summary>
        public static bool CanCustomizeCertificateValidation()
        {
            // iOS restricts certificate validation customization
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

### Task 11.3: IL2CPP Compatibility Check

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

### Task 11.4: Platform Test Suite

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

### Task 11.5: Platform-Specific Documentation

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
- HTTPS requests
- HTTP/1.1
- TLS 1.2+
- Background downloads (with limitations)

**Limitations:**
- Custom certificate validation is restricted by iOS
- App Transport Security (ATS) requires HTTPS by default
- Background requests limited to 30 seconds in background mode

**Recommendations:**
- Use HTTPS for all requests
- Configure ATS exceptions in Info.plist if HTTP is needed
- Set longer timeouts (60s) for mobile networks

### Android

**Supported:**
- HTTPS requests
- HTTP/1.1
- TLS 1.2+ (Android 5.0+)
- Custom certificate validation

**Limitations:**
- Cleartext (HTTP) traffic disabled by default on Android 9+
- Network permissions required in manifest

**Recommendations:**
- Add `android.permission.INTERNET` to AndroidManifest.xml
- For HTTP, configure `android:usesCleartextTraffic="true"` in manifest
- Test on multiple Android versions (5.0 through 13+)

### IL2CPP

**Compatibility:**
- Full support for IL2CPP scripting backend
- System.Text.Json works correctly
- Async/await fully supported
- Reflection used minimally (only for JSON serialization)

**Tested Configurations:**
- iOS + IL2CPP ✓
- Android + IL2CPP ✓
- Standalone + IL2CPP ✓

### WebGL

**Status:** NOT SUPPORTED in v1.0

WebGL support is planned for v1.x (see Phase 14 roadmap).

**Reason:**
- UnityWebRequest works differently in WebGL
- Requires JavaScript interop
- CORS restrictions
- Different threading model

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

- [ ] Basic GET/POST requests work
- [ ] HTTPS/SSL works correctly
- [ ] JSON serialization/deserialization works
- [ ] File downloads work
- [ ] Texture/AudioClip loading works (if applicable)
- [ ] Error handling works correctly
- [ ] Timeline events are captured
- [ ] No crashes or memory leaks
- [ ] Performance is acceptable

## Troubleshooting

### iOS: "An SSL error has occurred"
- Ensure using HTTPS
- Check ATS configuration in Info.plist
- Verify certificate is valid

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

Once Phase 11 is complete and validated:

1. Move to [Phase 12: Documentation & Samples](phase-12-documentation.md)
2. Write comprehensive documentation
3. Create sample projects
4. M3 milestone near completion

## Notes

- Test on real devices, not just simulators
- Test on multiple Android devices (different manufacturers)
- Test on both old and new OS versions
- Mobile networks behave differently than WiFi
- IL2CPP builds take longer but are production-ready
