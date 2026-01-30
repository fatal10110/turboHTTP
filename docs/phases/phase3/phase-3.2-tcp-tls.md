# Phase 3.2: TCP Connection Pool & TLS (Transport Assembly)

**Depends on:** Phase 2 (complete)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 2 new

---

## Step 1: `PooledConnection` + `TcpConnectionPool`

**File:** `Runtime/Transport/Tcp/TcpConnectionPool.cs`

### `PooledConnection`

Represents a single pooled TCP connection:
- Properties: `Socket` (Socket), `Stream` (Stream), `Host` (string), `Port` (int), `IsSecure` (bool)
- `LastUsed` — stored as `long _lastUsedTicks` with `Interlocked.Read/Exchange` for thread-safe 32-bit access
- `IsAlive` — detects server-closed connections, wrapped in try-catch for disposed sockets:
  ```csharp
  public bool IsAlive
  {
      get
      {
          try
          {
              if (Socket == null || !Socket.Connected) return false;
              return !(Socket.Poll(0, SelectMode.SelectRead) && Socket.Available == 0);
          }
          catch (ObjectDisposedException) { return false; }
      }
  }
  ```
- `Dispose()` — dispose `Stream` only. Since we use `NetworkStream(socket, ownsSocket: true)` and `SslStream(innerStream, leaveInnerStreamOpen: false)`, the disposal chain handles everything: SslStream → NetworkStream → Socket. No redundant socket dispose needed.

### `TcpConnectionPool`

Thread-safe pool keyed by `host:port:secure`:
- **Key normalization:** `host.ToLowerInvariant():port:{s|""}` (hostnames are case-insensitive per RFC 1035)
- **Data structures:**
  - `ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>` — idle connections
  - `ConcurrentDictionary<string, SemaphoreSlim>` — per-host connection limit enforcement

#### `GetConnectionAsync(host, port, secure, ct)`

1. Compute key, get/create semaphore for host
2. `await semaphore.WaitAsync(ct)` — blocks if at max connections
3. Try to dequeue idle connection:
   - Check `IsAlive` + idle timeout (`DateTime.UtcNow - LastUsed < _connectionIdleTimeout`)
   - If stale/dead: dispose and try next
4. If no reusable connection, create new:
   - **DNS resolution:** `var addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false)`
   - **Note:** `Dns.GetHostAddressesAsync` has no CancellationToken in .NET Standard 2.1. DNS resolution is not cancellable — timeout is enforced after DNS completes.
   - Pick first address (supports IPv6 — use `address.AddressFamily` not hardcoded `InterNetwork`)
   - `new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)`
   - `socket.NoDelay = true`
   - `await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false)`
   - `Stream stream = new NetworkStream(socket, ownsSocket: true)`
   - If secure: `stream = await TlsStreamWrapper.WrapAsync(stream, host, ct).ConfigureAwait(false)`
5. Return `new PooledConnection(socket, stream, host, port, secure)`
6. On exception: release semaphore, rethrow

#### `ReturnConnection(connection)`

1. If `_disposed` or connection null/dead → dispose connection, release semaphore, return
2. Update `connection.LastUsed`
3. Enqueue to idle pool
4. Release semaphore

#### `Dispose()`

1. Set `_disposed = true` (volatile bool)
2. Drain all queues, dispose each connection
3. Dispose all semaphores
4. Clear dictionaries

#### Configuration

- `maxConnectionsPerHost` (default 6, matches browser convention)
- `connectionIdleTimeout` (default 2 min)

---

## Step 2: `TlsStreamWrapper`

**File:** `Runtime/Transport/Tls/TlsStreamWrapper.cs`

Static utility for TLS handshake:

### `WrapAsync(Stream innerStream, string host, CancellationToken ct)`

Phase 3 uses the simple `AuthenticateAsClientAsync` overload (no ALPN needed):

```csharp
var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, ValidateServerCertificate);
await sslStream.AuthenticateAsClientAsync(
    host, null, SslProtocols.Tls12 | SslProtocols.Tls13, true
).ConfigureAwait(false);
```

Phase 3B will upgrade to `SslClientAuthenticationOptions` with ALPN for HTTP/2 negotiation.

### Post-handshake enforcement

After handshake, check `sslStream.SslProtocol`. If lower than TLS 1.2, throw `System.Security.SecurityException("TLS 1.2 or higher required")`.

### `GetNegotiatedProtocol(SslStream)`

Returns `"h2"`, `"http/1.1"`, or `null` based on `sslStream.NegotiatedApplicationProtocol`. Prepared for Phase 3B.

### `ValidateServerCertificate` callback

- Returns `sslPolicyErrors == SslPolicyErrors.None`
- Logs `sslPolicyErrors` flags for diagnostics when validation fails

---

## Verification

1. Both files compile in `TurboHTTP.Transport` assembly
2. `TcpConnectionPool` can create connections to a real host (manual test)
3. `TlsStreamWrapper` completes TLS handshake with `https://www.google.com`
4. TLS 1.2+ enforced post-handshake
5. IPv6 address resolution works (no hardcoded `AddressFamily.InterNetwork`)
6. Semaphore limits concurrent connections per host
7. Stale connections detected via `IsAlive` and disposed
8. Pool `Dispose()` cleans up all resources
