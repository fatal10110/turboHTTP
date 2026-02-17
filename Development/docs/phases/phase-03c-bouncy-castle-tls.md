# Phase 3C: BouncyCastle TLS Fallback for IL2CPP/AOT Platforms

**Milestone:** M0 (Spike)
**Dependencies:** Phase 3B (HTTP/2 Protocol Implementation)
**Estimated Complexity:** High
**Critical:** Yes - ALPN negotiation may fail on IL2CPP builds

## Problem Statement

The current TLS implementation uses `System.Net.Security.SslStream` with reflection-based ALPN negotiation (for HTTP/2 protocol selection). This approach has known risks:

1. **ALPN via reflection** - `SslClientAuthenticationOptions.ApplicationProtocols` and `SslStream.NegotiatedApplicationProtocol` are accessed via reflection since .NET Standard 2.1 doesn't expose them directly. This may fail on IL2CPP builds due to:
   - Code stripping removing the reflection targets
   - AOT compilation preventing runtime type resolution
   - Platform-specific TLS implementations (iOS Security.framework, Android BoringSSL) not supporting .NET's ALPN API

2. **Platform inconsistency** - Different platforms use different underlying TLS implementations:
   - **Editor/Standalone**: .NET Framework/Core TLS (ALPN usually works)
   - **iOS**: Security.framework via Xamarin bindings (ALPN support varies)
   - **Android**: BoringSSL or native TLS (ALPN support varies by API level)

## Proposed Solution

Implement BouncyCastle-based TLS as an optional fallback, following the pattern established by **Best HTTP** (a popular Unity networking library):

1. **Default path**: Continue using `SslStream` with reflection-based ALPN (works on most platforms)
2. **Fallback path**: If ALPN fails or is unavailable, automatically fall back to BouncyCastle TLS
3. **Configurable override**: Allow users to force BouncyCastle for specific scenarios

### Why BouncyCastle?

- **Pure C# implementation** - No platform-specific dependencies, works identically on all platforms
- **Full TLS 1.2/1.3 support** - Including ALPN extension for HTTP/2 negotiation
- **IL2CPP compatible** - No reflection, no runtime code generation
- **Battle-tested** - Used by Best HTTP and other Unity networking libraries
- **MIT Licensed** - Compatible with commercial Unity Asset Store distribution

## Architecture

### 3C.1: BouncyCastle Integration

**New Assembly:** `TurboHTTP.Transport.BouncyCastle.asmdef`

```
Runtime/Transport/BouncyCastle/
├── BouncyCastleTlsWrapper.cs      # Main TLS wrapper using BC
├── TurboTlsClient.cs               # BC TlsClient implementation
├── TurboTlsAuthentication.cs       # Server certificate validation
├── AlpnExtension.cs                # ALPN protocol negotiation
└── TlsProviderSelector.cs          # Auto-select SslStream vs BC
```

**Dependencies:**
- BouncyCastle Portable (NuGet package or Unity-specific distribution)
- Estimated additional size: ~1.5 MB (can be IL2CPP stripped if unused)

### 3C.2: TLS Provider Abstraction

**File:** `Runtime/Transport/Tls/ITlsProvider.cs`

```csharp
namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Abstraction over TLS implementation.
    /// Allows switching between SslStream and BouncyCastle.
    /// </summary>
    public interface ITlsProvider
    {
        /// <summary>
        /// Wrap a TCP stream with TLS, performing handshake.
        /// </summary>
        /// <param name="innerStream">Raw TCP stream</param>
        /// <param name="host">Server hostname for SNI and validation</param>
        /// <param name="alpnProtocols">ALPN protocols to negotiate (e.g., "h2", "http/1.1")</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>TLS-wrapped stream with ALPN result</returns>
        Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct);
    }

    public class TlsResult
    {
        public Stream SecureStream { get; set; }
        public string NegotiatedAlpn { get; set; }  // "h2", "http/1.1", or null
        public string TlsVersion { get; set; }       // "1.2", "1.3"
    }
}
```

### 3C.3: Provider Selection Logic

**File:** `Runtime/Transport/Tls/TlsProviderSelector.cs`

```csharp
public static class TlsProviderSelector
{
    public enum TlsBackend
    {
        Auto,           // Try SslStream, fall back to BC if ALPN fails
        SslStream,      // Force SslStream (may not support ALPN on some platforms)
        BouncyCastle    // Force BouncyCastle (guaranteed ALPN support)
    }

    /// <summary>
    /// Get the appropriate TLS provider based on platform and configuration.
    /// </summary>
    public static ITlsProvider GetProvider(TlsBackend backend = TlsBackend.Auto)
    {
        switch (backend)
        {
            case TlsBackend.SslStream:
                return SslStreamTlsProvider.Instance;

            case TlsBackend.BouncyCastle:
                return BouncyCastleTlsProvider.Instance;

            case TlsBackend.Auto:
            default:
                // Platform-specific auto-selection
                #if UNITY_IOS || UNITY_ANDROID
                    // On mobile, probe SslStream ALPN first, fall back to BC
                    if (!SslStreamTlsProvider.IsAlpnSupported())
                        return BouncyCastleTlsProvider.Instance;
                #endif
                return SslStreamTlsProvider.Instance;
        }
    }
}
```

### 3C.4: BouncyCastle TLS Implementation

**File:** `Runtime/Transport/BouncyCastle/BouncyCastleTlsProvider.cs`

```csharp
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace TurboHTTP.Transport.BouncyCastle
{
    public class BouncyCastleTlsProvider : ITlsProvider
    {
        public static readonly BouncyCastleTlsProvider Instance = new();

        public async Task<TlsResult> WrapAsync(
            Stream innerStream,
            string host,
            string[] alpnProtocols,
            CancellationToken ct)
        {
            var crypto = new BcTlsCrypto(new SecureRandom());
            var protocol = new TlsClientProtocol(innerStream);
            var client = new TurboTlsClient(crypto, host, alpnProtocols);

            // BouncyCastle handshake is blocking; run on thread pool
            await Task.Run(() => protocol.Connect(client), ct);

            return new TlsResult
            {
                SecureStream = protocol.Stream,
                NegotiatedAlpn = client.NegotiatedAlpn,
                TlsVersion = client.NegotiatedVersion
            };
        }
    }
}
```

## Tasks

### Task 3C.1: Add BouncyCastle Package Reference

**File:** `Runtime/Transport/BouncyCastle/TurboHTTP.Transport.BouncyCastle.asmdef`

```json
{
    "name": "TurboHTTP.Transport.BouncyCastle",
    "rootNamespace": "TurboHTTP.Transport.BouncyCastle",
    "references": [
        "TurboHTTP.Core",
        "TurboHTTP.Transport"
    ],
    "includePlatforms": [],
    "excludePlatforms": ["WebGL"],
    "allowUnsafeCode": false,
    "overrideReferences": true,
    "precompiledReferences": [
        "BouncyCastle.Cryptography.dll"
    ],
    "autoReferenced": false,
    "defineConstraints": [],
    "versionDefines": [],
    "noEngineReferences": true
}
```

### Task 3C.2: Implement ITlsProvider Interface

- Update `TlsStreamWrapper` to implement `ITlsProvider`
- Create `BouncyCastleTlsProvider` implementing `ITlsProvider`
- Implement `TlsProviderSelector` with platform detection

### Task 3C.3: Implement BouncyCastle TLS Client

- Create `TurboTlsClient` extending `DefaultTlsClient`
- Override `GetClientExtensions()` to include ALPN
- Override `NotifySelectedProtocol()` to capture ALPN result
- Implement certificate validation via `TurboTlsAuthentication`

### Task 3C.4: Update TcpConnectionPool

- Replace direct `TlsStreamWrapper` usage with `ITlsProvider`
- Add `TlsBackend` configuration option
- Handle ALPN result from `TlsResult`

### Task 3C.5: Platform Validation Matrix

| Platform | SslStream ALPN | BC TLS | Recommended |
|----------|----------------|--------|-------------|
| Editor (Windows) | ✅ Works | ✅ Works | SslStream |
| Editor (macOS) | ✅ Works | ✅ Works | SslStream |
| Editor (Linux) | ✅ Works | ✅ Works | SslStream |
| Standalone (Windows) | ✅ Works | ✅ Works | SslStream |
| Standalone (macOS) | ✅ Works | ✅ Works | SslStream |
| Standalone (Linux) | ⚠️ Mono-dependent | ✅ Works | BC fallback |
| iOS | ⚠️ Test required | ✅ Works | BC fallback |
| Android | ⚠️ Test required | ✅ Works | BC fallback |
| WebGL | ❌ N/A | ❌ N/A | N/A (deferred) |

### Task 3C.6: Add Configuration API

**File:** `Runtime/Core/HttpClientOptions.cs` (update)

```csharp
public class HttpClientOptions
{
    // ... existing options ...

    /// <summary>
    /// TLS backend selection. Auto tries SslStream first,
    /// falling back to BouncyCastle if ALPN is unavailable.
    /// </summary>
    public TlsBackend TlsBackend { get; set; } = TlsBackend.Auto;
}
```

## Validation Criteria

### Unit Tests

- [ ] BouncyCastle TLS handshake completes successfully
- [ ] ALPN negotiation returns "h2" when server supports HTTP/2
- [ ] ALPN negotiation returns "http/1.1" when server only supports HTTP/1.1
- [ ] Certificate validation rejects invalid certificates
- [ ] Certificate validation accepts valid certificates
- [ ] TLS 1.2 minimum enforced (TLS 1.0/1.1 rejected)

### Integration Tests

- [ ] HTTPS GET request via BouncyCastle completes successfully
- [ ] HTTP/2 request via BouncyCastle ALPN works (multiplexing, etc.)
- [ ] Fallback from SslStream to BouncyCastle works transparently
- [ ] Performance benchmark: BC vs SslStream (acceptable overhead)

### Platform Tests (Physical Devices)

- [ ] **iOS (physical device)**: HTTP/2 with ALPN via BC
- [ ] **Android (physical device)**: HTTP/2 with ALPN via BC
- [ ] **IL2CPP build**: No code stripping errors
- [ ] **AOT compilation**: No JIT dependencies

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| BouncyCastle size (~1.5 MB) | Bundle size increase | Make BC module optional, document stripping |
| BC performance overhead | Slower handshakes | Benchmark; recommend SslStream where it works |
| BC API changes | Breaking changes | Pin specific BC version, document upgrade path |
| Certificate pinning complexity | Security features | Defer to Phase 6 (Auth middleware) |

## Notes

- This phase is **optional but recommended** for production mobile deployments
- Users can exclude the BC assembly if they only target desktop platforms
- BC fallback is transparent — API remains identical regardless of backend
- The `TlsBackend.Auto` setting handles most cases automatically
- BouncyCastle Portable is actively maintained and widely used in Unity ecosystem

## References

- [BouncyCastle C# GitHub](https://github.com/bcgit/bc-csharp)
- [Best HTTP TLS Implementation](https://assetstore.unity.com/packages/tools/network/best-http-2-155981)
- [RFC 7301 - TLS ALPN Extension](https://tools.ietf.org/html/rfc7301)
- [Unity IL2CPP Restrictions](https://docs.unity3d.com/Manual/IL2CPP-BytecodeStripping.html)
