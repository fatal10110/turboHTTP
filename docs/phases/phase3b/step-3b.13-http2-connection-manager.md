# Step 3B.13: HTTP/2 Connection Manager

**File:** `Runtime/Transport/Http2/Http2ConnectionManager.cs`
**Depends on:** Step 3B.12 (Http2Connection)
**Spec:** RFC 7540 Section 9.1 (Connection Management)

## Purpose

Cache and manage HTTP/2 connections per host:port. Unlike HTTP/1.1 where each request gets its own connection from the pool, HTTP/2 multiplexes many requests over a single long-lived connection. This manager ensures one active `Http2Connection` per origin.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class Http2ConnectionManager : IDisposable
    {
        public Http2ConnectionManager();

        /// <summary>
        /// Get an existing alive h2 connection for this host:port, or null.
        /// </summary>
        public Http2Connection GetIfExists(string host, int port);

        /// <summary>
        /// Get or create an h2 connection for this host:port.
        /// If one exists and is alive, return it. Otherwise, create a new one.
        /// Prevents thundering herd via per-key locking.
        /// </summary>
        public Task<Http2Connection> GetOrCreateAsync(
            string host, int port, Stream tlsStream, CancellationToken ct);

        /// <summary>
        /// Remove and dispose a stale connection.
        /// </summary>
        public void Remove(string host, int port);

        public void Dispose();
    }
}
```

## Internal State

```csharp
private readonly ConcurrentDictionary<string, Http2Connection> _connections
    = new ConcurrentDictionary<string, Http2Connection>(StringComparer.OrdinalIgnoreCase);

private readonly ConcurrentDictionary<string, SemaphoreSlim> _initLocks
    = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
```

Key format: `"host:port"` (e.g., `"www.google.com:443"`).

## Method Details

### `GetIfExists(string host, int port)`

Fast-path for reusing an existing connection. No locking, no async:

```csharp
public Http2Connection GetIfExists(string host, int port)
{
    string key = $"{host}:{port}";
    if (_connections.TryGetValue(key, out var conn) && conn.IsAlive)
        return conn;
    return null;
}
```

### `GetOrCreateAsync(string host, int port, Stream tlsStream, CancellationToken ct)`

Handles the case where a new TLS connection has negotiated "h2" and needs an `Http2Connection`:

```csharp
public async Task<Http2Connection> GetOrCreateAsync(
    string host, int port, Stream tlsStream, CancellationToken ct)
{
    string key = $"{host}:{port}";

    // Fast path: existing alive connection
    if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
        return existing;

    // Slow path: create new connection with per-key lock to prevent thundering herd
    var initLock = _initLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    await initLock.WaitAsync(ct);
    try
    {
        // Double-check after acquiring lock
        if (_connections.TryGetValue(key, out existing) && existing.IsAlive)
            return existing;

        // Remove stale connection if present
        if (existing != null)
        {
            _connections.TryRemove(key, out _);
            existing.Dispose();
        }

        // Create and initialize new connection
        var conn = new Http2Connection(tlsStream, host, port);
        await conn.InitializeAsync(ct);
        _connections[key] = conn;
        return conn;
    }
    finally
    {
        initLock.Release();
    }
}
```

### `Remove(string host, int port)`

Called when a connection is detected as dead (GOAWAY, read loop failure, stale retry):

```csharp
public void Remove(string host, int port)
{
    string key = $"{host}:{port}";
    if (_connections.TryRemove(key, out var conn))
    {
        conn.Dispose();
    }
}
```

### `Dispose()`

```csharp
public void Dispose()
{
    foreach (var kvp in _connections)
    {
        kvp.Value.Dispose();
    }
    _connections.Clear();

    foreach (var kvp in _initLocks)
    {
        kvp.Value.Dispose();
    }
    _initLocks.Clear();
}
```

## Thundering Herd Prevention

When multiple requests arrive simultaneously for a host with no existing h2 connection, only the first request creates the connection. Others wait on the per-key `SemaphoreSlim` and then reuse the newly created connection.

Without this protection, N concurrent requests would create N TCP connections and N h2 connections, then discard N-1 of them — wasting resources and potentially confusing the server.

## Connection Lifecycle

```
1. First HTTPS request to host:port
   → TcpConnectionPool creates TCP + TLS connection
   → ALPN negotiates "h2"
   → RawSocketTransport calls _h2Manager.GetOrCreateAsync()
   → Http2Connection created and initialized (preface + SETTINGS)
   → Request sent as h2 stream

2. Subsequent requests to same host:port
   → RawSocketTransport calls _h2Manager.GetIfExists()
   → Returns existing alive connection (fast path)
   → Request sent as new h2 stream on same connection

3. Connection dies (GOAWAY, network error, read loop crash)
   → Http2Connection.IsAlive returns false
   → Next request falls through GetIfExists to pool path
   → New TLS connection created, new Http2Connection initialized
   → Old connection removed and disposed

4. Transport disposed
   → _h2Manager.Dispose() disposes all cached connections
```

## Thread Safety

- `ConcurrentDictionary` for lock-free reads (`GetIfExists` fast path).
- Per-key `SemaphoreSlim` for creation serialization (prevents thundering herd).
- `Http2Connection.IsAlive` is safe to read from any thread (volatile bool + completed task check).

## Validation Criteria

- [ ] First request creates connection, second reuses it
- [ ] `GetIfExists` returns null when no connection exists
- [ ] `GetIfExists` returns null when connection is not alive
- [ ] `GetOrCreateAsync` prevents thundering herd (concurrent calls for same host get same connection)
- [ ] `Remove` disposes the old connection
- [ ] Stale connection replaced by new one on next `GetOrCreateAsync`
- [ ] `Dispose` disposes all connections
- [ ] Case-insensitive host keys (`www.Google.com:443` == `www.google.com:443`)
