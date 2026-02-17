# Step 3B.14: RawSocketTransport Protocol Routing

**File:** `Runtime/Transport/RawSocketTransport.cs` (modify existing)
**Depends on:** Steps 3B.11 (PooledConnection changes), 3B.12 (Http2Connection), 3B.13 (Http2ConnectionManager)
**Spec:** RFC 7540 Section 3.3 (Starting HTTP/2 for "https" URIs)

## Purpose

Update `RawSocketTransport.SendAsync` to route requests to HTTP/2 or HTTP/1.1 based on ALPN negotiation results. HTTP/2 connections are reused across requests to the same origin; HTTP/1.1 connections use the existing pool-per-request model.

## Changes

### 1. Add Http2ConnectionManager Field

```csharp
private readonly Http2ConnectionManager _h2Manager = new Http2ConnectionManager();
```

### 2. Updated `SendAsync` Flow

The existing method structure (timeout enforcement, URI validation, exception mapping) remains unchanged. The core connection/send logic is updated:

```csharp
public async Task<UHttpResponse> SendAsync(
    UHttpRequest request, RequestContext context, CancellationToken ct)
{
    // ... existing validation and timeout setup (unchanged) ...

    try
    {
        // === NEW: HTTP/2 fast path (TLS only) ===
        if (secure)
        {
            var h2Conn = _h2Manager.GetIfExists(host, port);
            if (h2Conn != null)
            {
                try
                {
                    context.RecordEvent("TransportH2Reuse");
                    return await h2Conn.SendRequestAsync(request, context, timeoutCt);
                }
                catch (Exception) when (!timeoutCt.IsCancellationRequested)
                {
                    // Stale h2 connection — remove and fall through to pool path
                    _h2Manager.Remove(host, port);
                    context.RecordEvent("TransportH2StaleRetry");
                }
            }
        }

        // === Connection from pool ===
        context.RecordEvent("TransportConnecting");
        using var lease = await _pool.GetConnectionAsync(host, port, secure, timeoutCt);

        // === NEW: Protocol routing based on ALPN ===
        if (lease.Connection.NegotiatedAlpnProtocol == "h2")
        {
            // HTTP/2 path
            context.RecordEvent("TransportH2Init");

            // Transfer connection ownership to h2 manager
            lease.TransferOwnership();

            var h2Conn = await _h2Manager.GetOrCreateAsync(
                host, port, lease.Connection.Stream, timeoutCt);

            return await h2Conn.SendRequestAsync(request, context, timeoutCt);
        }
        else
        {
            // HTTP/1.1 path (existing logic, unchanged)
            return await SendHttp11Async(lease, request, context, timeoutCt);
        }
    }
    // ... existing exception mapping (unchanged) ...
}
```

### 3. Extract HTTP/1.1 Logic to `SendHttp11Async`

Move the existing HTTP/1.1 serialization/parsing logic into a private method for clarity:

```csharp
private async Task<UHttpResponse> SendHttp11Async(
    ConnectionLease lease, UHttpRequest request,
    RequestContext context, CancellationToken ct)
{
    // This is the existing logic, just moved into a method:
    context.RecordEvent("TransportSending");
    await Http11RequestSerializer.SerializeAsync(request, lease.Connection.Stream, ct);

    context.RecordEvent("TransportReceiving");
    var parsed = await Http11ResponseParser.ParseAsync(
        lease.Connection.Stream, request.Method, ct);

    if (parsed.KeepAlive)
        lease.ReturnToPool();

    context.RecordEvent("TransportComplete");

    return new UHttpResponse(
        statusCode: parsed.StatusCode,
        headers: parsed.Headers,
        body: parsed.Body,
        elapsedTime: context.Elapsed,
        request: request,
        error: null
    );
}
```

### 4. Stale h2 Connection Retry

When the h2 fast path fails (connection died since last check):
1. Remove the dead connection from `_h2Manager`
2. Fall through to the pool path
3. The pool creates a new TLS connection
4. ALPN renegotiates (may get "h2" again or fall back to "http/1.1")
5. Route based on new ALPN result

This mirrors the existing HTTP/1.1 stale-connection-retry pattern (retry for idempotent methods on `IOException` with `IsReused`).

**Note:** The h2 retry is not limited to idempotent methods because:
- The h2 connection manager tracks whether the connection is alive
- If the stream was already sent and GOAWAY'd, the `Http2Connection` itself handles stream-level retry semantics
- The retry here is at the connection level (create new connection), not the request level

### 5. Non-TLS Connections

Plain HTTP connections (`secure == false`) always use HTTP/1.1. The h2 fast path and ALPN check are bypassed entirely:

```csharp
if (secure)
{
    // h2 fast path...
}
// Pool path continues regardless
```

When the pool creates a non-TLS connection, `NegotiatedAlpnProtocol` is null, so the HTTP/1.1 path is taken.

### 6. Update `Dispose`

```csharp
public void Dispose()
{
    _h2Manager?.Dispose();  // NEW
    _pool?.Dispose();       // Existing
}
```

## Timeline Events

New timeline events for HTTP/2 paths:

| Event | When |
|-------|------|
| `TransportH2Reuse` | Reusing existing h2 connection (fast path) |
| `TransportH2Init` | First h2 connection to this host (ALPN negotiated "h2") |
| `TransportH2StaleRetry` | Stale h2 connection detected, retrying through pool |
| `TransportH2RequestSent` | H2 stream sent (from Http2Connection) |

Existing events (`TransportStart`, `TransportConnecting`, `TransportSending`, `TransportReceiving`, `TransportComplete`) continue to be used for the HTTP/1.1 path.

## Connection Ownership Model

```
HTTP/1.1 flow:
  Pool → Lease → Serialize/Parse → ReturnToPool or Dispose
  (Pool owns the connection lifecycle)

HTTP/2 flow:
  Pool → Lease → ALPN="h2" → TransferOwnership → Http2ConnectionManager
  (Manager owns the connection lifecycle)
  Subsequent requests → Manager.GetIfExists → SendRequestAsync
  (Pool is bypassed entirely)
```

## Error Mapping

HTTP/2 specific errors are mapped through `Http2Connection` to exceptions that match the existing error model:

| H2 Error | Exception | ErrorType |
|----------|-----------|-----------|
| GOAWAY | `UHttpException` | NetworkError |
| RST_STREAM(CANCEL) | `TaskCanceledException` | Cancelled |
| RST_STREAM(other) | `UHttpException` | NetworkError |
| COMPRESSION_ERROR | `UHttpException` | NetworkError |
| Connection died | `IOException` (from read loop) | NetworkError |

The existing exception mapping in `SendAsync` catches these and wraps them in `UHttpException` with appropriate `UHttpErrorType`.

## Validation Criteria

- [ ] HTTPS request to h2-capable server uses HTTP/2 (check timeline events)
- [ ] Second request to same host reuses h2 connection (fast path)
- [ ] HTTPS request to h1.1-only server uses HTTP/1.1
- [ ] HTTP (non-TLS) request always uses HTTP/1.1
- [ ] Stale h2 connection triggers retry through pool
- [ ] No regression on HTTP/1.1 behavior
- [ ] `Dispose` cleans up both h2 manager and pool
- [ ] Exception mapping works for h2 errors
- [ ] Timeline events recorded correctly for both paths
