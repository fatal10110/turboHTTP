# Phase 19a.6: TLS Provider Hardening (System-First, Safe Fallback)

**Depends on:** 19a.1 (`ArrayPool<byte>` completion)
**Estimated Effort:** 1 week

---

## Step 0: Align TLS Configuration With Existing `TlsBackend`

**Files modified:**
- `Runtime/Core/TlsBackend.cs`
- `Runtime/Core/UHttpClientOptions.cs`

Required behavior:

1. Keep `TlsBackend` as the public switch (`Auto`, `SslStream`, `BouncyCastle`).
2. Clarify `Auto` semantics in docs/comments:
   - Prefer `SslStream` on capable platforms.
   - Fall back to BouncyCastle only for capability/platform limitations.
3. Keep `SslStream` = strict system-only behavior.
4. Keep `BouncyCastle` = forced BC behavior.

---

## Step 1: Harden `TlsProviderSelector` Fallback Policy

**File:** `Runtime/Transport/Tls/TlsProviderSelector.cs` (modified)

Required behavior:

1. Fallback in `Auto` is allowed only when provider capability is unavailable (for example ALPN/platform limitations).
2. Do not fallback after certificate validation failure, hostname mismatch, or authentication failure.
3. Log selected provider at first use and fallback reason when capability fallback occurs.

Implementation constraints:

1. Preserve thread-safe provider caching.
2. Keep behavior deterministic across concurrent connections.

---

## Step 2: Harden `SslStreamTlsProvider`

**File:** `Runtime/Transport/Tls/SslStreamTlsProvider.cs` (modified)

Required behavior:

1. Keep TLS 1.2/1.3 configuration.
2. Keep ALPN negotiation behavior for HTTP/2 and HTTP/1.1.
3. Ensure authentication exceptions map cleanly to existing error model.
4. Ensure cert validation callback behavior remains strict by default.

Implementation constraints:

1. No permissive "trust-all" defaults.
2. No silent downgrade behavior on auth failures.

---

## Step 3: Add TLS Capability Diagnostics

**File:** `Runtime/Transport/Tls/TlsPlatformCapabilities.cs` (new)

Required behavior:

1. Expose capability summary for diagnostics (`IsSystemTlsAvailable`, provider description, ALPN support expectation).
2. Cache capability evaluation.
3. Provide helper output for startup diagnostics and tests.

Implementation constraints:

1. Detection logic is conservative and explicit.
2. Avoid repeated probing in hot paths.

---

## Step 4: Platform Matrix Validation

Required behavior:

1. Validate `TlsBackend.Auto`, `TlsBackend.SslStream`, and `TlsBackend.BouncyCastle` on supported targets.
2. Confirm fallback occurs for capability reasons only.
3. Confirm auth/cert failures fail closed (no fallback).
4. Validate IL2CPP mobile behavior and ALPN negotiation expectations.

---

## Verification Criteria

1. `Auto` selects system TLS first where supported.
2. `SslStream` strict mode behaves as expected.
3. `BouncyCastle` forced mode remains stable.
4. No downgrade on certificate/authentication failures.
5. Existing TLS tests pass for all three backend modes.
