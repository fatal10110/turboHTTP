# Phase 3C: BouncyCastle TLS Fallback

**Date**: 2026-02-06  
**Status**: ✅ Complete

## Objective
Implement BouncyCastle TLS as a fallback for IL2CPP/AOT platforms where `SslStream` ALPN may not work reliably.

## Key Decisions

1. **BouncyCastle as Source Code** - Included as repackaged source (not DLL) to avoid namespace conflicts. Uses `TurboHTTP.SecureProtocol.Org.BouncyCastle.*` namespace.

2. **TlsBackend Enum in Core** - Moved to `TurboHTTP.Core` to avoid circular assembly dependencies between Core and Transport.

3. **Selective Module Inclusion** - Only essential BouncyCastle folders: `tls`, `crypto`, `asn1`, `x509`, `math`, `security`, `util`. Excluded OpenPGP, CMS, etc.

4. **Reflection-Based Provider Loading** - `TlsProviderSelector` loads BouncyCastle via reflection, making the module optional for desktop-only projects.

## Files Created

### TLS Abstraction Layer (`Runtime/Transport/Tls/`)
- `ITlsProvider.cs` - Provider interface
- `TlsResult.cs` - Handshake result model
- `SslStreamTlsProvider.cs` - .NET SslStream implementation
- `TlsProviderSelector.cs` - Platform-based provider selection

### Core (`Runtime/Core/`)
- `TlsBackend.cs` - Enum: Auto, SslStream, BouncyCastle

### BouncyCastle Module (`Runtime/Transport/BouncyCastle/`)
- `TurboHTTP.Transport.BouncyCastle.asmdef` - Assembly definition
- `BouncyCastleTlsProvider.cs` - ITlsProvider implementation
- `TurboTlsClient.cs` - BouncyCastle TLS client
- `TurboTlsAuthentication.cs` - Certificate validation
- `Lib/` - Repackaged BouncyCastle source

### Editor (`Editor/`)
- `RepackageBouncyCastle.cs` - Unity menu tool

### Scripts (`scripts/`)
- `repackage_bouncycastle.sh` - Shell script for non-Unity repackaging

## Files Modified

- `UHttpClientOptions.cs` - Added `TlsBackend` property
- `TcpConnectionPool.cs` - Now uses `ITlsProvider` abstraction via `TlsProviderSelector`
- `PooledConnection` - Changed `NegotiatedTlsVersion` (SslProtocols?) to `TlsVersion` (string), added `TlsProviderName`
- `link.xml` - Added BouncyCastle IL2CPP preservation rules

## Files Removed

- `TlsStreamWrapper.cs` - Legacy TLS wrapper replaced by `ITlsProvider` abstraction

## Testing

Unit tests created in `Tests/Runtime/Transport/Tls/`:
- `TlsProviderSelectorTests.cs` - Provider selection logic (9 tests)
- `TlsResultTests.cs` - TlsResult data model (8 tests)
- `SslStreamProviderTests.cs` - SslStream provider (5 tests)
- `BouncyCastleProviderTests.cs` - BouncyCastle provider (5 tests)

## Usage

```csharp
// Auto-select (recommended)
var client = new UHttpClient();

// Force BouncyCastle
var options = new UHttpClientOptions { TlsBackend = TlsBackend.BouncyCastle };
var client = new UHttpClient(options);

// Direct provider access
var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
var result = await provider.WrapAsync(stream, "host", new[] { "h2" }, ct);
```
