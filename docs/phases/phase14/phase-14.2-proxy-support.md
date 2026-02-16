# Phase 14.2: Proxy Support

**Depends on:** Phase 14.1
**Assembly:** `TurboHTTP.Proxy`, `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files:** 3 new, 1 modified

---

## Step 1: Add Proxy Configuration Model

**Files:**
- `Runtime/Proxy/ProxySettings.cs` (new)
- `Runtime/Core/UHttpClientOptions.cs` (modify)

Required behavior:

1. Add explicit proxy settings (`Address`, `Credentials`, `BypassList`).
2. Support environment variable discovery (`HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`).
3. Support explicit opt-out to ignore environment proxy values.
4. Normalize and validate proxy URI schemes (`http`, `https`, `socks5` if supported).

Implementation constraints:

1. Keep settings immutable once request execution starts.
2. Do not leak credentials in diagnostics.
3. Preserve backward compatibility when proxy is unset.

---

## Step 2: Implement HTTP Proxy Routing and CONNECT Tunneling

**Files:**
- `Runtime/Proxy/ProxyConnector.cs` (new)
- `Runtime/Proxy/ProxyEnvironmentResolver.cs` (new)

Required behavior:

1. Route HTTP requests through proxy forward mode.
2. Use `CONNECT` tunneling for HTTPS destinations.
3. Inject `Proxy-Authorization` only for proxy endpoints.
4. Respect `NO_PROXY` and bypass list matching.
5. Surface deterministic errors for proxy auth failure and tunnel negotiation failures.

Implementation constraints:

1. Proxy connection lifecycle must integrate with existing transport pool rules.
2. CONNECT handshake should remain cancellation-safe.
3. Avoid cross-request credential bleed in pooled connections.

---

## Step 3: Add Deterministic Proxy Tests

**File:** `Tests/Runtime/Proxy/ProxySupportTests.cs` (new)

Required behavior:

1. Verify HTTP and HTTPS proxy paths independently.
2. Verify bypass matching behavior for host and CIDR-like patterns.
3. Verify credential header scoping and redaction in logs/events.
4. Verify environment-driven configuration precedence.

---

## Verification Criteria

1. Proxy behavior is opt-in and does not regress non-proxy flows.
2. CONNECT tunneling works with auth and clear failure reporting.
3. Environment-variable resolution is deterministic and overridable.
4. Proxy integration remains stable under cancellation and retries.
