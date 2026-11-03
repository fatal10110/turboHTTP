# Phase 18a.4: HTTP Proxy Tunneling

**Depends on:** Phase 18
**Assembly:** `TurboHTTP.WebSocket.Transport`
**Files:** 2 new, 2 modified
**Estimated Effort:** 3-4 days

---

## Motivation

Many enterprise networks, corporate firewalls, and mobile carriers route traffic through HTTP proxies. WebSocket connections fail silently if the client cannot tunnel through the proxy via HTTP CONNECT. This is a common production blocker for Unity apps deployed in corporate environments.

---

## Step 1: Implement Proxy Configuration

**File:** `Runtime/WebSocket.Transport/WebSocketProxySettings.cs` (new)

Required behavior:

1. Configuration with immutable types:
   - `ProxyUri` (Uri) — proxy endpoint (e.g., `http://proxy.corp:8080`). Validate scheme is `http://` only — HTTPS proxies are deferred.
   - `Credentials` (`ProxyCredentials?`, optional) — for proxy authentication (Basic only for v1).
   - `BypassList` (IReadOnlyList<string>) — hostnames/patterns that bypass the proxy. **Matching semantics:** exact hostname match (case-insensitive) and leading wildcard match (`*.domain` matches `foo.domain` and `bar.baz.domain`). CIDR notation and port-specific matching are deferred.
2. Define `ProxyCredentials` as a custom **readonly struct** (not `System.Net.NetworkCredential` which may be IL2CPP-stripped and is mutable):
   ```csharp
   public readonly struct ProxyCredentials
   {
       public string Username { get; }
       public string Password { get; }
   }
   ```
3. Static `None` property for no proxy.
4. Remove `UseSystemProxy` — system proxy detection is not reliably available across Unity platforms. Defer to a future phase with platform-specific implementations.

> [!WARNING]
> **Security consideration:** Basic proxy authentication sends credentials in Base64 (reversible encoding) over the unencrypted TCP connection to the proxy. Credentials are stored as plaintext `string` in managed memory. Document this limitation explicitly in API documentation and recommend HTTPS-based proxy solutions for sensitive environments.

---

## Step 2: Implement HTTP CONNECT Tunnel

**File:** `Runtime/WebSocket.Transport/ProxyTunnelConnector.cs` (new)

Required behavior:

1. After TCP connection to proxy, send `CONNECT host:port HTTP/1.1` request with `Host` header.
2. Parse proxy response: `200 Connection Established` → proceed with TLS/WebSocket handshake through the tunnel.
3. Handle proxy authentication:
   - `407 Proxy Authentication Required` → retry with `Proxy-Authorization: Basic <base64>` header.
   - Only **Basic** authentication scheme for v1. Digest auth is deferred (complex nonce handling).
4. Timeout enforcement via `CancellationToken`.
5. Tunnel stream wraps the original TCP stream — transparent to the WebSocket handshake layer.
6. Log a warning when Basic auth credentials are sent over an unencrypted proxy connection.

---

## Step 3: Integrate into Transport and Add Error Types

**Files:** `Runtime/WebSocket.Transport/RawSocketWebSocketTransport.cs` (modify), `Runtime/WebSocket/WebSocketConnectionOptions.cs` (modify)

Required behavior:

1. Add `ProxySettings` property to `WebSocketConnectionOptions` (default: `WebSocketProxySettings.None`).
2. When proxy is configured, `RawSocketWebSocketTransport.ConnectAsync` connects to proxy first, runs the CONNECT tunnel, then proceeds with optional TLS handshake and WebSocket upgrade.
3. TLS handshake happens **after** tunnel establishment (the proxy sees only encrypted traffic for `wss://`).
4. **Proxy-specific error codes** in `WebSocketError` enum:
   - `ProxyAuthenticationRequired` — 407 received and no credentials configured.
   - `ProxyConnectionFailed` — cannot reach the proxy endpoint.
   - `ProxyTunnelFailed` — CONNECT tunnel rejected (non-200 response from proxy).

---

## Verification Criteria

1. CONNECT tunnel establishment with mock proxy.
2. Proxy authentication: 407 → retry with Basic auth credentials.
3. TLS over proxy tunnel for `wss://`.
4. Proxy bypass list: exact hostname match and `*.domain` wildcard match.
5. `ProxyCredentials` immutability.
6. Proxy-specific `WebSocketError` codes: `ProxyAuthenticationRequired`, `ProxyConnectionFailed`, `ProxyTunnelFailed`.
7. Security warning logged when Basic auth used over unencrypted proxy.
