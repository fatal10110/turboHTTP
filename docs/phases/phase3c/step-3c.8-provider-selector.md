# Step 3C.8: TlsProviderSelector

**File:** `Runtime/Transport/Tls/TlsProviderSelector.cs`  
**Depends on:** Steps 3C.3, 3C.7  
**Spec:** Platform Detection

## Purpose

Provide automatic selection logic to choose between `SslStreamTlsProvider` and `BouncyCastleTlsProvider` based on platform capabilities and user configuration.

## Types to Implement

### `TlsBackend` (enum)

```csharp
namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// TLS backend selection strategy.
    /// </summary>
    public enum TlsBackend
    {
        /// <summary>
        /// Automatically select the best TLS provider for the current platform.
        /// Tries SslStream first; falls back to BouncyCastle if ALPN is unsupported.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Force use of System.Net.Security.SslStream.
        /// May not support ALPN on all platforms (IL2CPP, mobile).
        /// </summary>
        SslStream = 1,

        /// <summary>
        /// Force use of BouncyCastle pure C# TLS implementation.
        /// Guaranteed ALPN support on all platforms.
        /// </summary>
        BouncyCastle = 2
    }
}
```

### `TlsProviderSelector` (static class)

```csharp
using System;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Selects the appropriate TLS provider based on platform and configuration.
    /// </summary>
    public static class TlsProviderSelector
    {
        /// <summary>
        /// Get the TLS provider for the specified backend strategy.
        /// </summary>
        public static ITlsProvider GetProvider(TlsBackend backend = TlsBackend.Auto)
        {
            switch (backend)
            {
                case TlsBackend.SslStream:
                    return GetSslStreamProvider();

                case TlsBackend.BouncyCastle:
                    return GetBouncyCastleProvider();

                case TlsBackend.Auto:
                default:
                    return GetAutoProvider();
            }
        }

        private static ITlsProvider GetSslStreamProvider()
        {
            return SslStreamTlsProvider.Instance;
        }

        private static ITlsProvider GetBouncyCastleProvider()
        {
            // Use reflection to check if BouncyCastle assembly is available
            // This allows the BouncyCastle module to be optional
            // Note: Requires [Preserve] attribute on BouncyCastleTlsProvider for IL2CPP
            var bcType = Type.GetType(
                "TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, TurboHTTP.Transport.BouncyCastle",
                throwOnError: false);

            if (bcType == null)
            {
                throw new InvalidOperationException(
                    "BouncyCastle TLS provider is not available. " +
                    "Ensure TurboHTTP.Transport.BouncyCastle assembly is included in your project.");
            }

            // Get singleton instance via reflection
            var instanceProperty = bcType.GetProperty("Instance",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

            return (ITlsProvider)instanceProperty.GetValue(null);
        }

        private static ITlsProvider GetAutoProvider()
        {
            // Platform-specific auto-selection logic

#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
            // Desktop platforms: SslStream works reliably
            return GetSslStreamProvider();

#elif UNITY_IOS || UNITY_ANDROID
            // Mobile platforms: Check if SslStream supports ALPN
            var sslStreamProvider = SslStreamTlsProvider.Instance;
            if (sslStreamProvider.IsAlpnSupported())
            {
                // ALPN works via reflection, use SslStream
                return sslStreamProvider;
            }
            else
            {
                // ALPN not supported, fall back to BouncyCastle
                try
                {
                    return GetBouncyCastleProvider();
                }
                catch (InvalidOperationException)
                {
                    // BouncyCastle not available, use SslStream anyway
                    // (ALPN will be null, HTTP/1.1 fallback)
                    #if UNITY_2017_1_OR_NEWER
                        UnityEngine.Debug.LogWarning(
                            "BouncyCastle TLS provider not available. " +
                            "ALPN negotiation may not work. HTTP/2 will be unavailable.");
                    #else
                        System.Diagnostics.Debug.WriteLine(
                            "[TurboHTTP] WARNING: BouncyCastle TLS provider not available. " +
                            "ALPN negotiation may not work. HTTP/2 will be unavailable.");
                    #endif
                    return sslStreamProvider;
                }
            }

#elif UNITY_STANDALONE_LINUX
            // Linux: Prefer BouncyCastle due to Mono inconsistencies
            try
            {
                return GetBouncyCastleProvider();
            }
            catch (InvalidOperationException)
            {
                // BouncyCastle not available, fall back to SslStream
                return GetSslStreamProvider();
            }

#else
            // Unknown platform: Try SslStream
            return GetSslStreamProvider();
#endif
        }
    }
}
```

## Implementation Details

### Auto Selection Logic

**Platform-specific strategy:**

| Platform | Auto Behavior |
|----------|---------------|
| **Windows Editor/Standalone** | SslStream (native Schannel TLS) |
| **macOS Editor/Standalone** | SslStream (native SecureTransport TLS) |
| **Linux Standalone** | BouncyCastle (Mono TLS is inconsistent) |
| **iOS** | Check ALPN support → BouncyCastle if unavailable |
| **Android** | Check ALPN support → BouncyCastle if unavailable |
| **Other** | SslStream (fallback) |

### BouncyCastle Availability Check

Uses **reflection** to check if the BouncyCastle assembly is present:
- `Type.GetType("TurboHTTP.Transport.BouncyCastle.BouncyCastleTlsProvider, ...")`
- If type not found → assembly not included → throw exception or fallback

This allows the BouncyCastle module to be **optional**. Desktop-only projects can exclude it entirely.

### Singleton Access

Both providers are singletons:
- `SslStreamTlsProvider.Instance` (direct reference, always available)
- `BouncyCastleTlsProvider.Instance` (via reflection, optional)

### Fallback Behavior

On mobile platforms:
1. Check `SslStreamTlsProvider.IsAlpnSupported()`
2. If `true` → use SslStream (best performance)
3. If `false` → try BouncyCastle
4. If BouncyCastle not available → use SslStream anyway (ALPN will be null, HTTP/1.1 only)

### Unity Conditional Compilation

Uses Unity's platform defines:
- `UNITY_EDITOR`, `UNITY_STANDALONE_WIN`, `UNITY_STANDALONE_OSX`
- `UNITY_IOS`, `UNITY_ANDROID`
- `UNITY_STANDALONE_LINUX`

This ensures correct provider is selected at compile time.

## Configuration API

Users can override auto-selection:

```csharp
var options = new HttpClientOptions
{
    TlsBackend = TlsBackend.BouncyCastle  // Force BouncyCastle
};

var client = new HttpClient(options);
```

## Edge Cases

### BouncyCastle Not Available

If user requests `TlsBackend.BouncyCastle` but assembly is not included:
- `GetBouncyCastleProvider()` throws `InvalidOperationException`
- Clear error message guides user to include the assembly

### ALPN Not Supported, BouncyCastle Not Available

On mobile with forced `TlsBackend.SslStream` and no ALPN support:
- ALPN will be `null` in `TlsResult`
- HTTP/2 will be unavailable (fallback to HTTP/1.1)
- Log warning to inform developer

## Namespace

`TurboHTTP.Transport.Tls`

## Validation Criteria

- [ ] Enum and class compile without errors
- [ ] Auto selection works correctly on each platform
- [ ] Forcing a specific backend works
- [ ] BouncyCastle availability check works correctly
- [ ] Appropriate warnings/errors when BouncyCastle unavailable
- [ ] No Unity engine references (except Debug.LogWarning in platform-specific code)

## Testing Notes

Test on all platforms:
1. **Windows standalone**: Should use SslStream
2. **iOS device**: Should use BouncyCastle (assuming no ALPN)
3. **Android device**: Should use BouncyCastle (assuming no ALPN)
4. **iOS without BouncyCastle**: Should fall back to SslStream with warning

## References

- [Unity Platform Defines](https://docs.unity3d.com/Manual/PlatformDependentCompilation.html)
- [Type.GetType() Documentation](https://docs.microsoft.com/en-us/dotnet/api/system.type.gettype)
