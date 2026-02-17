# Phase 14.2: Proxy Support

**Depends on:** Phase 14.1
**Assembly:** `TurboHTTP.Proxy`, `TurboHTTP.Transport`, `TurboHTTP.Tests.Runtime`
**Files:** 6 new, 2 modified

---

## Step 1: Add Proxy Configuration Model

**Files:**
- `Runtime/Proxy/ProxySettings.cs` (new)
- `Runtime/Proxy/ProxyBypassMatcher.cs` (new)
- `Runtime/Core/UHttpClientOptions.cs` (modify)

### Technical Spec

Core model:

```csharp
public sealed class ProxySettings
{
    public Uri Address { get; init; }
    public NetworkCredential Credentials { get; init; }
    public IReadOnlyList<string> BypassList { get; init; }
    public bool UseEnvironmentVariables { get; init; } = true;
    public bool AllowPlaintextProxyAuth { get; init; } = false;
}
```

Configuration precedence:

1. Per-request override on `RequestContext` (if present).
2. `UHttpClientOptions.Proxy`.
3. Environment variables (`HTTPS_PROXY` for HTTPS targets, `HTTP_PROXY` for HTTP targets, fallback to lowercase variants).
4. No proxy.

`NO_PROXY` and bypass matching rules:

1. Exact host match (`example.com`).
2. Domain suffix match (`.example.com` matches subdomains and apex).
3. Wildcard prefix token (`*.corp.local`) normalized to suffix rule.
4. IPv4 literal and IPv6 literal exact match.
5. Optional CIDR support for IP targets only.
6. Port-sensitive rule (`example.com:8443`) if port specified.

Validation and normalization:

1. Support `http://` proxy URI for forward proxy and CONNECT.
2. Reject unsupported URI schemes at options-validation time.
3. Normalize default ports (`80`, `443`) in proxy endpoint keying.
4. Treat credentials as sensitive data; no raw values in logs or exceptions.

### Implementation Constraints

1. Proxy settings snapshot must be immutable for a request lifetime.
2. Environment resolution occurs once per request and is cached in context.
3. `NO_PROXY` evaluation must be case-insensitive for hostnames.
4. Existing non-proxy request path must not allocate proxy objects when disabled.

---

## Step 2: Implement HTTP Forwarding and HTTPS CONNECT Tunneling

**Files:**
- `Runtime/Proxy/ProxyConnector.cs` (new)
- `Runtime/Proxy/ProxyEnvironmentResolver.cs` (new)
- `Runtime/Transport/RawSocketTransport.cs` (modify)

### Technical Spec

Required APIs:

```csharp
public interface IProxyConnector
{
    Task<ProxyConnectionResult> ConnectAsync(
        Uri targetUri,
        ProxySettings settings,
        CancellationToken cancellationToken);
}
```

HTTP target via proxy:

1. Connect TCP socket to proxy endpoint.
2. Send absolute-form request line (`GET http://host/path HTTP/1.1`).
3. Send `Proxy-Authorization` only when credentials configured and transport policy allows.
4. Keep origin `Authorization` header untouched.

HTTPS target via proxy:

1. Connect TCP socket to proxy endpoint.
2. Send `CONNECT host:port HTTP/1.1` with `Host` header.
3. Handle `407 Proxy Authentication Required`:
   - if credentials available and not yet attempted, retry once with proxy auth header;
   - otherwise fail with deterministic auth exception.
4. On `200 Connection Established`, upgrade to TLS on tunneled socket.
5. Ensure SNI and certificate validation use origin host, not proxy host.

Pooling and keying:

1. Pool key must include proxy endpoint + auth identity hash + target scheme.
2. CONNECT tunnel sockets are bound to target authority; do not reuse tunnel across different authorities unless explicitly designed.
3. Ensure pooled socket metadata records whether it is direct or proxy-backed.

Error mapping:

| Condition | Exception Type | Mandatory Metadata |
|---|---|---|
| Proxy unreachable | `ConnectionException` | proxy endpoint, socket error |
| CONNECT 407 | `ProxyAuthException` | proxy endpoint, auth attempted flag |
| CONNECT non-200 | `ProxyTunnelException` | status code, reason phrase, headers |
| TLS over tunnel failure | existing TLS exception path | target authority + proxy endpoint |

### Implementation Constraints

1. CONNECT handshake parser must enforce response-header size caps.
2. No credential value logging; only username presence flag.
3. Cancellation during CONNECT must dispose socket immediately.
4. Proxy auth retry count is exactly one to avoid loops.

---

## Step 3: Add Deterministic Proxy Tests

**File:** `Tests/Runtime/Proxy/ProxySupportTests.cs` (new)

### Required Test Matrix

| Case | Setup | Expected Result |
|---|---|---|
| `HttpViaProxy_UsesAbsoluteForm` | HTTP target + proxy | request line uses absolute URI |
| `HttpsViaProxy_ConnectSuccess` | CONNECT returns 200 | tunneled TLS flow succeeds |
| `Connect407_RetryWithAuthOnce` | first 407 then 200 | exactly one retry with proxy auth |
| `Connect407_NoCredentialsFails` | 407 and no creds | `ProxyAuthException` |
| `NoProxyBypass_SkipsProxy` | host matches bypass | direct transport path used |
| `EnvProxyPrecedence_ResolvesCorrectly` | mixed options/env | precedence rules honored |
| `CancellationDuringConnect_NoLeaks` | cancel during tunnel handshake | socket disposed, deterministic cancel |

---

## Verification Criteria

1. Proxy support is strictly opt-in and does not alter direct-path behavior when disabled.
2. CONNECT tunneling is protocol-correct, cancellation-safe, and auth-safe.
3. Bypass and environment resolution are deterministic and fully test-covered.
4. Sensitive proxy credentials never appear in logs, exceptions, or monitor events.
