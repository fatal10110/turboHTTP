# Phase 3C Implementation Plan — Overview

Phase 3C implements BouncyCastle TLS as a fallback for IL2CPP/AOT platforms where SslStream ALPN may not work reliably. All new code goes under `Runtime/Transport/Tls/` and `Runtime/Transport/BouncyCastle/`. Two existing files are modified.

## Step Index

| Step | Name | Files | Depends On |
|---|---|---|---|
| [3C.1](step-3c.1-itls-provider.md) | ITlsProvider Interface | 1 new | — |
| [3C.2](step-3c.2-tls-result.md) | TlsResult Model | 1 new | — |
| [3C.3](step-3c.3-sslstream-provider.md) | SslStream TLS Provider | 1 new | 3C.1, 3C.2 |
| [3C.4](step-3c.4-bouncycastle-package.md) | BouncyCastle Package Integration | 1 new (asmdef) | — |
| [3C.5](step-3c.5-turbo-tls-client.md) | BouncyCastle TurboTlsClient | 1 new | 3C.4 |
| [3C.6](step-3c.6-tls-authentication.md) | BouncyCastle TlsAuthentication | 1 new | 3C.4 |
| [3C.7](step-3c.7-bouncycastle-provider.md) | BouncyCastle TLS Provider | 1 new | 3C.1, 3C.2, 3C.4, 3C.5, 3C.6 |
| [3C.8](step-3c.8-provider-selector.md) | TlsProviderSelector | 1 new | 3C.3, 3C.7 |
| [3C.9](step-3c.9-tcp-pool-update.md) | Update TcpConnectionPool | 1 modified | 3C.1, 3C.2, 3C.8 |
| [3C.10](step-3c.10-config-api.md) | Configuration API | 1 modified | 3C.8 |
| [3C.11](step-3c.11-tests.md) | Unit & Integration Tests | 6 new | 3C.1–3C.10 |

## Dependency Graph

```
No dependencies (parallel):
    ├── 3C.1 ITlsProvider
    ├── 3C.2 TlsResult
    └── 3C.4 BouncyCastle package setup

Layer 2 (Core Providers):
    ├── 3C.3 SslStreamTlsProvider    ← 3C.1, 3C.2
    ├── 3C.5 TurboTlsClient          ← 3C.4
    └── 3C.6 TurboTlsAuthentication  ← 3C.4

Layer 3 (BouncyCastle Provider):
    └── 3C.7 BouncyCastleTlsProvider ← 3C.1, 3C.2, 3C.4, 3C.5, 3C.6

Layer 4 (Selection):
    └── 3C.8 TlsProviderSelector     ← 3C.3, 3C.7

Layer 5 (Integration):
    ├── 3C.9 TcpConnectionPool       ← 3C.1, 3C.2, 3C.8
    └── 3C.10 HttpClientOptions      ← 3C.8

Layer 6 (Validation):
    └── 3C.11 Tests                  ← ALL above
```

Steps in Layer 1 have no inter-dependencies and can be implemented in parallel. Step 3C.7 is the largest implementation file but has clear interfaces to follow.

## New Directory Structure

```
Runtime/Transport/Tls/
    ITlsProvider.cs              — Step 3C.1
    TlsResult.cs                 — Step 3C.2
    SslStreamTlsProvider.cs      — Step 3C.3
    TlsProviderSelector.cs       — Step 3C.8

Runtime/Transport/BouncyCastle/
    TurboHTTP.Transport.BouncyCastle.asmdef  — Step 3C.4
    TurboTlsClient.cs            — Step 3C.5
    TurboTlsAuthentication.cs    — Step 3C.6
    BouncyCastleTlsProvider.cs   — Step 3C.7
```

## Modified Files

| File | Step | Changes |
|------|------|---------|
| `Runtime/Transport/Tcp/TcpConnectionPool.cs` | 3C.9 | Replace `TlsStreamWrapper` with `ITlsProvider` abstraction, handle `TlsResult` |
| `Runtime/Core/HttpClientOptions.cs` | 3C.10 | Add `TlsBackend` enum property for provider selection |

## Exclusions (NOT Implemented in Phase 3C)

- **Certificate pinning:** Deferred to Phase 6 (Auth middleware)
- **Custom certificate validation callbacks:** Basic validation only
- **TLS 1.3 specific features:** Support TLS 1.3 but don't expose session tickets, 0-RTT, etc.
- **Client certificates:** Server cert validation only, no mutual TLS
- **OCSP stapling:** Not implemented
- **Session resumption:** Let the TLS implementation handle it transparently

## Implementation Notes

- **Optional module:** The BouncyCastle assembly should be optional. Projects targeting only desktop can exclude it.
- **Automatic fallback:** `TlsBackend.Auto` mode automatically selects the best provider for the platform.
- **No breaking changes:** Existing code continues to work; this phase only adds new capabilities.
- **IL2CPP safe:** All BouncyCastle code is AOT-friendly with no reflection.

> [!IMPORTANT]
> **BouncyCastle is included as SOURCE CODE with a custom namespace**, not as a DLL. This follows the proven BestHTTP approach:
> - **Namespace:** `TurboHTTP.SecureProtocol.Org.BouncyCastle.*` (instead of `Org.BouncyCastle.*`)
> - **Why:** Prevents conflicts with other Unity plugins (Firebase, encryption tools) that use standard BouncyCastle
> - **Benefits:** IL2CPP can strip unused algorithms, reducing final app size by 60-70%
> - **See:** Step 3C.4 for detailed repackaging instructions


## Validation Platform Matrix

| Platform | SslStream ALPN | BC TLS | Recommended | Test Required |
|----------|----------------|--------|-------------|---------------|
| Editor (Windows) | ✅ Works | ✅ Works | SslStream | No |
| Editor (macOS) | ✅ Works | ✅ Works | SslStream | No |
| Editor (Linux) | ✅ Works | ✅ Works | SslStream | No |
| Standalone (Windows) | ✅ Works | ✅ Works | SslStream | No |
| Standalone (macOS) | ✅ Works | ✅ Works | SslStream | No |
| Standalone (Linux) | ⚠️ Mono-dependent | ✅ Works | BC fallback | Yes |
| iOS | ⚠️ Test required | ✅ Works | BC fallback | **Yes** |
| Android | ⚠️ Test required | ✅ Works | BC fallback | **Yes** |
| WebGL | ❌ N/A | ❌ N/A | N/A | No |
