# Step 3B.11: PooledConnection & ConnectionLease Changes

**File:** `Runtime/Transport/Tcp/TcpConnectionPool.cs` (modify existing)
**Depends on:** Nothing
**Spec:** RFC 7540 Section 3.3 (ALPN), RFC 7301 (TLS ALPN Extension)

## Purpose

Update the existing connection pool infrastructure to support HTTP/2:
1. Track the ALPN-negotiated protocol on each connection
2. Pass ALPN protocol preferences during TLS handshake
3. Allow `ConnectionLease` ownership transfer for long-lived HTTP/2 connections

## Changes

### 1. Add `NegotiatedAlpnProtocol` to `PooledConnection`

Add a new property to track the ALPN negotiation result:

```csharp
// In PooledConnection class:

/// <summary>
/// The ALPN-negotiated protocol ("h2", "http/1.1", or null if no ALPN).
/// Set after TLS handshake completes.
/// </summary>
public string NegotiatedAlpnProtocol { get; internal set; }
```

**Location:** Add alongside the existing `NegotiatedTlsVersion` property, which already tracks TLS version.

### 2. Pass ALPN Protocols to TLS Handshake

In `TcpConnectionPool.CreateConnectionAsync`, after creating the TCP socket and before TLS wrapping, pass the ALPN protocol list to `TlsStreamWrapper.WrapAsync`:

**Current code:**
```csharp
var tlsResult = await TlsStreamWrapper.WrapAsync(networkStream, host, ct);
```

**Updated code:**
```csharp
var tlsResult = await TlsStreamWrapper.WrapAsync(networkStream, host, ct,
    new[] { "h2", "http/1.1" });
```

`TlsStreamWrapper.WrapAsync` already accepts `string[] alpnProtocols = null` as the 4th parameter — this is a drop-in change. When the TLS handshake completes, ALPN negotiation selects one of the offered protocols based on server support.

### 3. Store ALPN Result After TLS Handshake

After `TlsStreamWrapper.WrapAsync` returns, call `GetNegotiatedProtocol` and store the result:

```csharp
var tlsResult = await TlsStreamWrapper.WrapAsync(networkStream, host, ct,
    new[] { "h2", "http/1.1" });

connection.Stream = tlsResult.Stream;
connection.NegotiatedTlsVersion = /* existing logic */;

// NEW: Store ALPN result
if (tlsResult.Stream is System.Net.Security.SslStream sslStream)
{
    connection.NegotiatedAlpnProtocol = TlsStreamWrapper.GetNegotiatedProtocol(sslStream);
}
```

`TlsStreamWrapper.GetNegotiatedProtocol` already exists and returns the negotiated protocol string. It uses the `SslStream.NegotiatedApplicationProtocol` property via reflection (for .NET Standard 2.1 compatibility).

**Possible values:**
- `"h2"` — Server supports HTTP/2
- `"http/1.1"` — Server supports HTTP/1.1 only
- `null` — No ALPN support (server or runtime doesn't support it)

### 4. Add `TransferOwnership()` to `ConnectionLease`

HTTP/2 connections are long-lived and shared across requests. When a new TLS connection negotiates "h2", the `Http2ConnectionManager` takes ownership of the underlying stream. The `ConnectionLease` must release the semaphore permit without closing the connection.

```csharp
// In ConnectionLease class:

/// <summary>
/// Transfer ownership of the underlying connection to another owner (e.g., Http2ConnectionManager).
/// The connection will NOT be disposed or returned to the idle pool when this lease is disposed.
/// The semaphore permit IS still released.
/// </summary>
public void TransferOwnership()
{
    lock (_lock)
    {
        if (_released)
            return;
        _released = true;
        // Do NOT dispose the connection — the new owner manages it.
        // Do NOT return to pool — the connection is no longer pooled.
    }
    // Release the semaphore permit so other connections can be created for this host
    _semaphore?.Release();
}
```

**Behavior comparison:**

| Method | Connection | Semaphore | Use Case |
|--------|-----------|-----------|----------|
| `Dispose()` | Disposed | Released | Normal cleanup (error, no keep-alive) |
| `ReturnToPool()` | Returned to idle queue | Released | HTTP/1.1 keep-alive |
| `TransferOwnership()` | Neither disposed nor returned | Released | HTTP/2 handoff |

**Why release the semaphore?** The per-host semaphore limits concurrent connections in the creation phase. Once an h2 connection is established, it handles its own concurrency via `MAX_CONCURRENT_STREAMS`. The semaphore slot should be freed so the pool can create new connections for other protocols or after h2 connection failure.

### 5. Idempotency

`TransferOwnership()` is idempotent (guarded by `_released` flag), consistent with `Dispose()` and `ReturnToPool()`. Calling it after `Dispose()` or `ReturnToPool()` is a no-op.

## Impact on Existing Behavior

- **HTTP/1.1 connections:** No behavior change. ALPN will negotiate "http/1.1" (or null on old servers). `TransferOwnership()` is never called.
- **Non-TLS connections:** `NegotiatedAlpnProtocol` remains null. Always HTTP/1.1.
- **Existing tests:** Should still pass — ALPN is additive, and `TransferOwnership()` is unused by existing code paths.

## Validation Criteria

- [ ] `NegotiatedAlpnProtocol` is set after TLS handshake
- [ ] ALPN protocols ["h2", "http/1.1"] are passed to `TlsStreamWrapper.WrapAsync`
- [ ] `TransferOwnership()` prevents connection disposal on lease `Dispose()`
- [ ] `TransferOwnership()` releases semaphore
- [ ] `TransferOwnership()` is idempotent
- [ ] `TransferOwnership()` + `Dispose()` = semaphore released exactly once
- [ ] Non-TLS connections have `NegotiatedAlpnProtocol == null`
- [ ] Existing HTTP/1.1 behavior is unchanged
