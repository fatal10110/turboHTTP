# Phase 22b.2: Streaming Through Proxy Connections

**Depends on:** Phase 22a (complete), benefits from 22b.1 for 100-continue through proxies
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 0 new, 2 modified

---

## Step 1: HTTP Forward Proxy — Body Transfer (No Copy)

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified) — `PrepareHttpProxyForwardRequest`

### Current State

The forward proxy path creates a new `UHttpRequest` and copies the body with `request.Body.ToArray()`:

```csharp
// Current:
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Body.IsEmpty ? null : request.Body.ToArray(), request.Timeout, metadata);
```

### Change

Transfer `request.Content` (the `UHttpRequestBody`) instead of copying:

```csharp
// After 22b.2:
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Content, request.Timeout, metadata);
```

The proxy only changes the request-line format (absolute-form URI) and adds `Proxy-Authorization`. The body source is the same — no reason to copy.

### Request-Line Serialization

The current `Http11RequestSerializer` checks `RequestMetadataKeys.ProxyAbsoluteForm` to format the request line as absolute-form (`GET http://example.com/path HTTP/1.1`). This must continue working with the 22a serialization split (and 22b.1's `SerializeHeadersAsync` / `SerializeBodyAsync` if completed). Verify no regression.

---

## Step 2: HTTPS CONNECT Tunnel — Body Transfer (No Copy)

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified) — `PrepareHttpsProxyTunnelRequest`

### Current State

Same body copy issue:

```csharp
// Current:
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Body.IsEmpty ? null : request.Body.ToArray(), request.Timeout, metadata);
```

### Change

```csharp
// After 22b.2:
return new UHttpRequest(request.Method, request.Uri, headers,
    request.Content, request.Timeout, metadata);
```

---

## Step 3: Streaming Dispatch Through Tunnels

**File:** `Runtime/Transport/RawSocketTransport.cs` (modified) — `DispatchViaProxyAsync`

### Current State

After the CONNECT handshake establishes a tunnel and TLS is negotiated, the proxy code dispatches via `DispatchOnStreamAsync`. Post-22a, this is replaced by the streaming dispatch path.

### Change

Ensure the streaming dispatch path is used for both:
1. **HTTP forward proxy** — after connection to proxy, use streaming dispatch with the forwarded request
2. **HTTPS CONNECT tunnel** — after tunnel establishment + TLS handshake, use streaming dispatch on the tunneled stream

The tunneled stream is functionally identical to a direct TLS connection. The streaming dispatch path works unchanged.

### `ConnectionLease` Ownership for Streaming Responses

For streaming responses through CONNECT tunnels:

1. The lease is for the **proxy connection** (proxy host:port), not the origin server
2. After TLS handshake, the TLS-wrapped stream is handed to the streaming dispatch
3. `ConnectionLease.TransferOwnership()` transfers the proxy connection lease to the streaming response
4. When the streaming response is disposed, the proxy connection (with its TLS wrapper) is returned to the pool or closed

**Critical implementation change:** The existing `DispatchViaProxyAsync` uses `using var lease` which auto-disposes the lease at method scope exit. For streaming responses, the lease must outlive the method. The `using` pattern must be replaced with manual lease management:

```csharp
// BEFORE (current — broken for streaming):
using var lease = await _pool.GetConnectionAsync(proxyHost, proxyPort, secure: false, ct);
// ... dispatch ... lease auto-disposed when method returns

// AFTER (22b.2 — streaming-safe):
var lease = await _pool.GetConnectionAsync(proxyHost, proxyPort, secure: false, ct);
try
{
    // ... dispatch, transfer ownership to streaming response body source ...
    // If streaming: lease ownership transferred, do NOT dispose here
    // If buffered or error: dispose lease in catch/finally
}
catch
{
    lease.Dispose();
    throw;
}
```

This is the same ownership-transfer pattern used in `DispatchOnStreamAsync` for direct connections. The proxy path must mirror it exactly.

### Connection Pool Key

The pool uses `(host, port)` as the connection key. For CONNECT tunnels:
- Key remains `(proxyHost, proxyPort)` for the outer connection
- The inner TLS session is tied to the origin, but the socket-level connection is to the proxy
- Proxy connections to different origins through the same proxy cannot be reused (each CONNECT tunnel is origin-specific)
- This is the current behavior and is correct per RFC
- **Verify:** If the pool key is `(proxyHost, proxyPort)` only, two concurrent CONNECT tunnels to different origins through the same proxy would conflict. The pool key must include the origin to prevent incorrect tunnel reuse. Confirm the existing implementation handles this (e.g., by not returning CONNECT tunnels to the pool, or by using a composite key).

---

## Step 4: Connection Reuse Through Tunnels

After a streaming response through a CONNECT tunnel is fully consumed:

### HTTP/1.1 Inner Connection

Same drain-or-close rules as direct HTTP/1.1:
- If the inner HTTP/1.1 connection can be reused (keep-alive, body fully drained), the tunnel remains open and the lease is returned to the pool
- The next request to the same origin through the same proxy can reuse the tunnel
- If the inner connection cannot be reused, the tunnel is closed and the lease is discarded

### HTTP/2 Through CONNECT Tunnels

The current code forces HTTP/1.1 on the tunnel framing. The *inner* TLS connection to the origin server can negotiate HTTP/2 via ALPN. This is correct — the proxy tunnel is HTTP/1.1 but the end-to-end protocol can be HTTP/2. **No changes needed for 22b.**

### Early-Dispose Policy

If the streaming response is disposed before the body is fully consumed:
- Follow the same drain-or-close policy as direct connections
- If remaining bytes ≤ `BufferedDrainReuseThresholdBytes`, drain and return the lease
- Otherwise, close the tunnel

---

## Step 5: CONNECT Tunnel Body Handling (No Change)

The CONNECT handshake itself is always bodyless — it's a tunneling mechanism, not a content request. The existing `DrainProxyConnectBodyAsync` handles any unexpected body in the CONNECT response (e.g., error pages). **No changes needed.**

---

## Step 6: `Expect: 100-continue` Through Proxies

If 22b.1 is completed before 22b.2:

- **HTTP forward proxy:** The 100-continue flow works normally — the proxy forwards the `Expect` header and the origin server's 100/final response is relayed back through the proxy. No special handling.
- **HTTPS CONNECT tunnel:** After tunnel establishment, the inner connection is direct to the origin. 100-continue works exactly as for direct connections.

If 22b.1 is NOT yet completed, this step is a no-op (no regression).

---

## Step 7: Tests

**File:** `Tests/Runtime/Transport/ProxyStreamingTests.cs` (new)

### Unit Tests (with `MockTransport`)

1. **Forward proxy streaming upload** — verify body source streamed (no `ToArray()` copy), bounded memory
2. **Forward proxy streaming download** — verify streaming response through proxy
3. **CONNECT tunnel streaming upload** — verify body source streamed through tunnel
4. **CONNECT tunnel streaming download** — verify streaming response through tunnel, bounded memory
5. **Connection reuse through tunnel** — fully consume streaming response, verify tunnel lease returned to pool
6. **Early-dispose through tunnel** — dispose before body consumed, verify drain-or-close policy applied
7. **`ConnectionLease` ownership transfer** — verify lease transferred from proxy dispatch to streaming response
8. **Absolute-form URI serialization** — verify request-line format correct for forward proxy with streaming body

### Conditional Tests (if 22b.1 complete)

9. **100-continue through forward proxy** — verify 100-continue flow works through proxy
10. **100-continue through CONNECT tunnel** — verify 100-continue flow works through tunnel

### Regression Tests

11. **Buffered body through forward proxy** — verify `BufferedRequestBody` (non-streaming) fast path still works correctly after `ToArray()` removal. The `ResolveBodyWriteMode` path in the serializer must still use the buffered fast path for known-length bodies.

### Memory Tests

12. **Large upload through forward proxy** — verify memory does NOT grow proportional to body size
13. **Large download through CONNECT tunnel** — verify memory bounded during streaming download

---

## Files Impacted (Summary)

| File | Change |
|------|--------|
| `Runtime/Transport/RawSocketTransport.cs` | `DispatchViaProxyAsync`: use streaming dispatch; `PrepareHttpProxyForwardRequest`: transfer `request.Content`; `PrepareHttpsProxyTunnelRequest`: transfer `request.Content` |
| `Runtime/Transport/Http1/Http11RequestSerializer.cs` | Verify absolute-form URI serialization works with serialization split |
| `Tests/Runtime/Transport/ProxyStreamingTests.cs` | New test file |

## Completion Criteria

- [ ] HTTP forward proxy request uses streaming body source (no `Body.ToArray()` copy)
- [ ] HTTPS CONNECT tunnel request uses streaming body source (no `Body.ToArray()` copy)
- [ ] Streaming response through CONNECT tunnel correctly transfers `ConnectionLease` ownership
- [ ] Connection reuse after fully-consumed streaming response through tunnel works correctly
- [ ] Early-dispose of streaming response through tunnel follows drain-or-close policy
- [ ] Memory behavior through proxy matches direct connection (bounded, not proportional to payload size)
- [ ] Large upload through HTTP forward proxy does not allocate `O(body)` extra memory
- [ ] Large download through HTTPS CONNECT tunnel streams with bounded memory
- [ ] `Expect: 100-continue` works correctly through proxy connections (if 22b.1 complete)
- [ ] Absolute-form URI serialization works with the serialization split
- [ ] Buffered (non-streaming) body through forward proxy still uses fast path (regression)
- [ ] Pool key for CONNECT tunnels prevents incorrect tunnel reuse across different origins
- [ ] All unit and memory tests pass (11–13 tests)

## IL2CPP / Platform Notes

- Proxy connections go through the same `TcpConnectionPool` and `TlsProviderSelector` as direct connections. No new platform-specific concerns.
- `SslStream` through a CONNECT tunnel is the same as direct `SslStream` — the inner TLS handshake is to the origin server, not the proxy.
