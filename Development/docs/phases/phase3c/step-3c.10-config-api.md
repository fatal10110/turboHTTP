# Step 3C.10: Configuration API

**File:** `Runtime/Core/HttpClientOptions.cs` (MODIFY)  
**Depends on:** Step 3C.8  
**Spec:** Public Configuration API

## Purpose

Expose the `TlsBackend` configuration option in `HttpClientOptions`, allowing users to control which TLS provider is used.

## Changes Required

### Add TlsBackend Property

**File:** `Runtime/Core/HttpClientOptions.cs`

**Add this property:**

```csharp
/// <summary>
/// TLS backend selection strategy.
/// Default is Auto, which selects the best provider for the current platform.
/// </summary>
/// <remarks>
/// - Auto: Try SslStream first, fall back to BouncyCastle if ALPN unavailable
/// - SslStream: Force use of System.Net.Security.SslStream (may not support ALPN on all platforms)
/// - BouncyCastle: Force use of BouncyCastle TLS (guaranteed ALPN support everywhere)
/// </remarks>
public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;
```

### Add Using Statement

At the top of the file:

```csharp
using TurboHTTP.Transport.Tls;
```

## Full Example

**Updated `HttpClientOptions` class** (partial):

```csharp
using System;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Core
{
    public class HttpClientOptions
    {
        // ... existing options ...

        /// <summary>
        /// Maximum number of concurrent connections per host.
        /// Default: 6 (matches browser behavior).
        /// </summary>
        public int MaxConnectionsPerHost { get; set; } = 6;

        /// <summary>
        /// TLS backend selection strategy.
        /// Default is Auto, which selects the best provider for the current platform.
        /// </summary>
        /// <remarks>
        /// - Auto: Try SslStream first, fall back to BouncyCastle if ALPN unavailable
        /// - SslStream: Force use of System.Net.Security.SslStream (may not support ALPN on all platforms)
        /// - BouncyCastle: Force use of BouncyCastle TLS (guaranteed ALPN support everywhere)
        /// 
        /// Note: Advanced security features (certificate pinning, custom validation callbacks)
        /// are planned for Phase 6 (Advanced Middleware) and not yet available.
        /// </remarks>
        public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;

        // ... other options ...
    }
}
```

## Usage Examples

### Default (Auto)

```csharp
var client = new HttpClient();  // Uses TlsBackend.Auto by default
```

### Force SslStream

```csharp
var options = new HttpClientOptions
{
    TlsBackend = TlsBackend.SslStream
};
var client = new HttpClient(options);
```

### Force BouncyCastle

```csharp
var options = new HttpClientOptions
{
    TlsBackend = TlsBackend.BouncyCastle
};
var client = new HttpClient(options);
```

## Documentation

Add to user documentation:

---

### TLS Backend Selection

TurboHTTP supports two TLS backends:

1. **SslStream** (default on desktop): Uses .NET's built-in TLS implementation
   - ✅ Faster performance (native code)
   - ⚠️ ALPN may not work on IL2CPP builds (iOS, Android)

2. **BouncyCastle** (fallback on mobile): Pure C# TLS implementation
   - ✅ Guaranteed ALPN support on all platforms
   - ✅ IL2CPP/AOT compatible
   - ⚠️ Slower performance (~2-3x handshake time)

**Auto mode** (recommended):
- Desktop platforms: Uses SslStream
- Mobile platforms: Checks ALPN support, uses BouncyCastle if needed

**Manual selection**:
```csharp
var options = new HttpClientOptions
{
    TlsBackend = TlsBackend.BouncyCastle  // Force BouncyCastle everywhere
};
```

---

## Migration Notes

### Existing Code

No changes required. Existing code will use `TlsBackend.Auto` by default.

### New Code

Developers can now explicitly control TLS backend:
- Testing: Force BouncyCastle to verify IL2CPP compatibility
- Desktop-only: Force SslStream to exclude BouncyCastle module (smaller build)

## Validation Criteria

- [ ] Property added to `HttpClientOptions`
- [ ] Default value is `TlsBackend.Auto`
- [ ] XML documentation is complete
- [ ] Using statement added for `TurboHTTP.Transport.Tls`
- [ ] No breaking changes to existing API

## Testing Notes

Test that option is respected:
1. Create `HttpClient` with `TlsBackend.SslStream`
2. Verify that SslStream provider is used (check logs or TlsResult.ProviderName)
3. Repeat with `TlsBackend.BouncyCastle`

## References

- Step 3C.8: `TlsBackend` enum definition
- Step 3C.9: Usage in `TcpConnectionPool`
