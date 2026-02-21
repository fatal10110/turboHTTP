# Phase 19a.6: System TLS Priority with BouncyCastle Fallback

**Depends on:** 19a.1 (`ArrayPool<byte>` Completion Sweep)
**Estimated Effort:** 1 week

---

## Step 0: Define `TlsProviderMode` Configuration

**File:** `Runtime/Core/TurboHttpConfig.cs` (modified)

Required behavior:

1. Add `TlsProviderMode` enum:
    ```csharp
    public enum TlsProviderMode
    {
        SystemPreferred, // Default: use SslStream, fall back to BC if unavailable
        SystemOnly,      // Require SslStream; throw if unavailable
        BouncyCastleOnly // Force BC (existing behavior, for backward compat)
    }
    ```
2. Add `TlsProviderMode TlsMode` property to `TurboHttpConfig` (default: `SystemPreferred`).
3. Document that `SystemPreferred` attempts `SslStream` first, falling back to BouncyCastle on `PlatformNotSupportedException`.

Implementation constraints:

1. Immutable after config freeze (existing pattern).
2. `BouncyCastleOnly` preserves exact current behavior — zero changes to existing TLS path.
3. `SystemOnly` throws `PlatformNotSupportedException` if `SslStream` is unavailable.

---

## Step 1: Implement `SystemTlsStreamProvider`

**File:** `Runtime/Transport/SystemTlsStreamProvider.cs` (new)

Required behavior:

1. Implement the same `ITlsProvider` interface as the existing BouncyCastle provider.
2. Wrap `SslStream` with `SslClientAuthenticationOptions` for TLS 1.2/1.3.
3. Support ALPN protocol negotiation (for HTTP/2 `h2` and HTTP/1.1 `http/1.1`).
4. Support custom certificate validation callback matching existing BC behavior.
5. Support certificate pinning via the same configuration as BC.
6. Support client certificates if configured.

Implementation constraints:

1. Use `SslStream(innerStream, leaveInnerStreamOpen: false)` to ensure socket cleanup.
2. Set `SslClientAuthenticationOptions.EnabledSslProtocols` to `SslProtocols.Tls12 | SslProtocols.Tls13`.
3. Map `RemoteCertificateValidationCallback` to match existing BC validation behavior.
4. Handle `AuthenticationException` and wrap in `UHttpException` with appropriate error codes.
5. Do NOT modify any BouncyCastle source code.

---

## Step 2: Implement TLS Provider Selection & Fallback

**File:** `Runtime/Transport/TlsProviderSelector.cs` (modified)

Required behavior:

1. Update `TlsProviderSelector` to respect `TlsProviderMode`:
   - `SystemPreferred`: try `SystemTlsStreamProvider` first; on `PlatformNotSupportedException` or TLS negotiation failure, fall back to BC with a diagnostic log.
   - `SystemOnly`: use `SystemTlsStreamProvider` only; throw on failure.
   - `BouncyCastleOnly`: use existing BC provider only (current behavior).
2. Runtime detection: attempt `SslStream` negotiation; catch platform exceptions for fallback.
3. Cache the detection result per platform (static) — don't re-detect on every connection.

Implementation constraints:

1. Fallback must be transparent to the caller — same `ITlsProvider` interface regardless of provider.
2. Log the selected provider at `Info` level on first use.
3. Log fallback events at `Warning` level with the exception details.
4. Detection cache must be thread-safe (use `Lazy<T>` or `Volatile`).

---

## Step 3: Integrate Certificate Validation for SslStream

**File:** `Runtime/Transport/SystemTlsCertificateValidator.cs` (new)

Required behavior:

1. Bridge the existing BC certificate validation configuration to `SslStream`'s `RemoteCertificateValidationCallback`.
2. Support custom root CA certificates (same as BC provider).
3. Support certificate pinning (public key or thumbprint matching).
4. Match error mapping: `SslPolicyErrors` → `UHttpError` codes consistent with BC.

Implementation constraints:

1. Callback must handle `SslPolicyErrors.None`, `RemoteCertificateNotAvailable`, `RemoteCertificateNameMismatch`, and `RemoteCertificateChainErrors`.
2. Certificate pinning check runs AFTER chain validation.
3. Do NOT trust all certificates by default — enforce validation unless explicitly configured.

---

## Step 4: Platform TLS Compatibility Matrix Validation

**File:** `Runtime/Transport/TlsPlatformCapabilities.cs` (new)

Required behavior:

1. Define a static class that reports platform TLS capabilities:

| Platform | TLS Provider | Expected Result |
|---|---|---|
| Windows (Mono/IL2CPP) | `SslStream` (SChannel) | Hardware-accelerated TLS |
| macOS (Mono) | `SslStream` (SecureTransport) | Hardware-accelerated TLS |
| Linux (Mono/IL2CPP) | `SslStream` (OpenSSL) | Hardware-accelerated TLS |
| Android (IL2CPP) | `SslStream` (platform) | Native TLS |
| iOS (IL2CPP) | `SslStream` (SecureTransport) | Hardware-accelerated TLS |
| WebGL | Browser TLS | Neither BC nor SslStream |
| Custom embedded | BouncyCastle | Fallback |

2. Expose `IsSystemTlsAvailable` (bool) and `SystemTlsDescription` (string) for diagnostics.

Implementation constraints:

1. Use `RuntimeInformation.IsOSPlatform()` for detection.
2. Cache results in static readonly fields.
3. WebGL detection: check for Browser JS interop availability or known `#if UNITY_WEBGL` define.

---

## Verification Criteria

1. `SystemTlsStreamProvider` successfully negotiates TLS 1.2/1.3 on Windows, macOS, Linux.
2. ALPN negotiation works correctly for HTTP/2 (`h2`) and HTTP/1.1.
3. Certificate validation matches existing BC behavior (custom CAs, pinning).
4. `SystemPreferred` mode falls back to BC with diagnostic log when SslStream is unavailable.
5. `SystemOnly` mode throws when SslStream is unavailable.
6. `BouncyCastleOnly` mode uses BC exclusively (exact current behavior).
7. All existing TLS tests pass with all three `TlsProviderMode` values.
8. Memory profiler on `SystemPreferred` shows elimination of BC's 1000+ `new byte[]` allocations.
9. IL2CPP AOT validation on iOS and Android with `SystemPreferred` mode.
10. Platform capability detection is accurate on all supported platforms.
