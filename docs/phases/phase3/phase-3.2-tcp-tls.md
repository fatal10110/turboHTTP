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
- `IsAlive` — **best-effort** detection of server-closed connections. For TLS connections, `Socket.Available` does not reflect SslStream's internal buffering, so this check may miss some states. The retry-on-stale mechanism in `RawSocketTransport` (Phase 3.4) is the true safety net. Wrapped in try-catch for disposed sockets:
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
   - **Note:** `Dns.GetHostAddressesAsync` has no CancellationToken in .NET Standard 2.1. DNS resolution is not cancellable — timeout is enforced after DNS completes. On mobile networks (captive portals, WiFi↔cellular transitions), DNS can hang for 30+ seconds consuming the entire request timeout. **Known limitation** — see CLAUDE.md "Critical Risk Areas".
   - **Address selection with fallback:** Try addresses in order. If `ConnectAsync` fails with `SocketException` on the first address, try the next (supports IPv6 — use `address.AddressFamily` not hardcoded `InterNetwork`). This is a "Happy Eyeballs lite" approach — full RFC 8305 deferred to Phase 10.
     ```csharp
     SocketException lastException = null;
     foreach (var address in addresses)
     {
         try
         {
             var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
             socket.NoDelay = true;
             await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
             return socket; // success
         }
         catch (SocketException ex)
         {
             lastException = ex;
         }
     }
     throw lastException;
     ```
   - `new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)`
   - `socket.NoDelay = true`
   - `await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false)`
   - `Stream stream = new NetworkStream(socket, ownsSocket: true)`
   - If secure: wrap in try-catch to prevent socket leak on TLS handshake failure:
     ```csharp
     try
     {
         stream = await TlsStreamWrapper.WrapAsync(stream, host, ct).ConfigureAwait(false);
     }
     catch
     {
         stream.Dispose(); // Cascades to NetworkStream → Socket
         throw;
     }
     ```
5. Return `new PooledConnection(socket, stream, host, port, secure)`
6. On exception: release semaphore, rethrow

#### `ReturnConnection(connection)`

**Critical ordering:** Enqueue MUST happen BEFORE semaphore release. Otherwise a waiting thread can wake up, find an empty queue, and create a new connection — exceeding the per-host limit.

1. If `_disposed` or connection null/dead → dispose connection, release semaphore, return
2. Update `connection.LastUsed`
3. **Enqueue to idle pool** ← must be before step 4
4. **Release semaphore** ← only after connection is available in queue

#### `Dispose()`

1. Set `_disposed = true` (volatile bool)
2. Drain all queues, dispose each connection
3. Dispose all semaphores
4. Clear dictionaries

#### Configuration

Constructor parameters (allow tuning without waiting for Phase 10):
```csharp
public TcpConnectionPool(int maxConnectionsPerHost = 6, TimeSpan? connectionIdleTimeout = null)
{
    _maxConnectionsPerHost = maxConnectionsPerHost;
    _connectionIdleTimeout = connectionIdleTimeout ?? TimeSpan.FromMinutes(2);
}
```

- `maxConnectionsPerHost` (default 6, matches browser convention)
- `connectionIdleTimeout` (default 2 min)

**Known limitation:** `ConcurrentDictionary<string, SemaphoreSlim>` creates a semaphore per unique host:port:secure key and never removes them. For apps making requests to many unique hostnames (e.g., CDN subdomains), this is a slow memory leak. Cleanup deferred to Phase 10 background idle task.

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

### Post-handshake enforcement (defensive only)

Since `AuthenticateAsClientAsync` is called with `SslProtocols.Tls12 | SslProtocols.Tls13`, the handshake itself rejects TLS 1.0/1.1 — the server gets `AuthenticationException`, not a downgrade. The post-handshake check is **redundant** but kept as a defensive assertion:

After handshake, check `sslStream.SslProtocol`. If lower than TLS 1.2, throw `System.Security.SecurityException("TLS 1.2 or higher required")`. This should never trigger in practice.

**Platform note:** Consider using `SslProtocols.None` (OS negotiates best available) as the safer cross-platform default, with the post-handshake TLS 1.2 minimum check as the enforcement mechanism. This is the Microsoft-recommended approach and avoids issues with `SslProtocols.Tls13` availability on older macOS/iOS versions. See WARNING-1 in review notes.

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
