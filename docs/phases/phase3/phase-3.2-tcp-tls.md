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
- `LastUsed` — stored as `long _lastUsedTicks` with `Interlocked.Read/Exchange` for thread-safe 64-bit atomicity (required on 32-bit platforms where `long` read/write is NOT atomic without `Interlocked`)
- `IsAlive` — **best-effort** detection of server-closed connections. For TLS connections, `Socket.Available` does not reflect SslStream's internal buffering, so this check may miss some states. The retry-on-stale mechanism in `RawSocketTransport` (Phase 3.4) is the true safety net. Returns `false` after disposal (guards against `ObjectDisposedException`). **Must never be called after `Dispose()` in normal flow** — the `ConnectionLease` class ensures `ReturnToPool()` (which calls `IsAlive`) is always called before `Dispose()`.
  ```csharp
  private bool _disposed;

  public bool IsAlive
  {
      get
      {
          if (_disposed) return false;
          try
          {
              if (Socket == null || !Socket.Connected) return false;
              return !(Socket.Poll(0, SelectMode.SelectRead) && Socket.Available == 0);
          }
          catch (ObjectDisposedException) { return false; }
          catch (SocketException) { return false; }
      }
  }
  ```
- `Dispose()` — dispose `Stream` only. Since we use `NetworkStream(socket, ownsSocket: true)` and `SslStream(innerStream, leaveInnerStreamOpen: false)`, the disposal chain handles everything: SslStream → NetworkStream → Socket. No redundant socket dispose needed.

### `ConnectionLease` (IDisposable class)

**Critical design:** Wraps a `PooledConnection` + semaphore reference to guarantee the per-host semaphore permit is **always** released, regardless of success, failure, or keep-alive status. This prevents the deadlock that would occur if permits leaked on non-keepalive responses or exception paths.

**Why a class, not a struct:** A mutable `IDisposable` struct is unsafe in C#. Structs have value-type copy semantics — if the struct is accidentally copied (passed by value, boxed, assigned to another variable), each copy has independent `_released` state. With `using var`, the compiler may operate on a copy, so `ReturnToPool()` would modify the local while `Dispose()` runs on the compiler's copy where `_released` is still `false`. This causes: (1) connections always destroyed even after ReturnToPool, (2) double semaphore release if both copies are disposed. A class (reference type) ensures all operations affect the same instance. The ~32-byte heap allocation per request is well within the GC budget.

```csharp
public sealed class ConnectionLease : IDisposable
{
    private readonly TcpConnectionPool _pool;
    private readonly SemaphoreSlim _semaphore;
    private readonly object _lock = new object();
    public PooledConnection Connection { get; }
    private bool _released;
    private bool _disposed;

    internal ConnectionLease(TcpConnectionPool pool, SemaphoreSlim semaphore, PooledConnection connection)
    {
        _pool = pool;
        _semaphore = semaphore;
        Connection = connection;
    }

    /// <summary>
    /// Return the connection to the pool for keep-alive reuse.
    /// Must be called BEFORE Dispose() for the connection to be reused.
    /// If not called, Dispose() will destroy the connection (but still release the semaphore).
    /// Thread-safe: synchronized with Dispose() to prevent races on async continuations.
    /// </summary>
    public void ReturnToPool()
    {
        lock (_lock)
        {
            if (!_released && !_disposed && Connection.IsAlive)
            {
                _pool.EnqueueConnection(Connection);
                _released = true;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return; // Idempotent — prevents double semaphore release
            _disposed = true;

            if (!_released)
            {
                Connection?.Dispose();
            }
        }
        // Release semaphore OUTSIDE the lock to avoid holding lock during Release().
        // ALWAYS release semaphore — this is the critical invariant.
        // Every WaitAsync() must have exactly one Release(), regardless of
        // whether the connection was returned to pool, disposed, or errored.
        try
        {
            _semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            // Pool was disposed while connection was in flight — safe to ignore.
        }
    }
}
```

**Usage pattern in `RawSocketTransport.SendAsync`:**
```csharp
using var lease = await _pool.GetConnectionAsync(host, port, secure, ct);
// ... serialize, parse ...
if (parsed.KeepAlive)
    lease.ReturnToPool();
// Dispose() runs unconditionally: releases semaphore, disposes connection if not returned
```

### `TcpConnectionPool`

Thread-safe pool keyed by `host:port:secure`:
- **Key normalization:** `host:port:{s|""}` — use `StringComparer.OrdinalIgnoreCase` in dictionaries instead of `host.ToLowerInvariant()` to avoid one string allocation per request. Hostnames are case-insensitive per RFC 1035.
- **Data structures:**
  - `ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>(StringComparer.OrdinalIgnoreCase)` — idle connections
  - `ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase)` — per-host connection limit enforcement

#### `GetConnectionAsync(host, port, secure, ct)` → returns `ConnectionLease`

1. Compute key, get/create semaphore for host
2. `await semaphore.WaitAsync(ct)` — blocks if at max connections
3. **From this point, the semaphore permit is owned. ALL paths must release it.** The returned `ConnectionLease` guarantees this via its `Dispose()` method.
4. Try to dequeue idle connection (inside try-catch that releases semaphore on failure):
   - Check `IsAlive` + idle timeout (`DateTime.UtcNow - LastUsed < _connectionIdleTimeout`)
   - If stale/dead: dispose and try next
5. If no reusable connection, create new:
   - **DNS resolution with timeout wrapper:**
     `Dns.GetHostAddressesAsync` has no CancellationToken in .NET Standard 2.1. To prevent 30+ second hangs on mobile networks, wrap with a timeout:
     ```csharp
     private const int DnsTimeoutMs = 5000; // 5-second DNS timeout

     var dnsTask = Dns.GetHostAddressesAsync(host);
     var timeoutTask = Task.Delay(DnsTimeoutMs, ct);
     var completed = await Task.WhenAny(dnsTask, timeoutTask).ConfigureAwait(false);

     ct.ThrowIfCancellationRequested();

     if (completed == timeoutTask)
         throw new UHttpException(new UHttpError(UHttpErrorType.Timeout,
             $"DNS resolution for '{host}' timed out after {DnsTimeoutMs}ms"));

     var addresses = await dnsTask.ConfigureAwait(false);
     ```
     **Note:** The background DNS task continues running after timeout (unavoidable in .NET Std 2.1). But the user-facing request fails fast instead of hanging indefinitely.
   - **Socket.ConnectAsync timeout:** Uses `cancellationToken.Register(() => socket.Dispose())` pattern for best-effort cancellation.
   - **Address selection with fallback (IPv6-safe):** Try addresses in DNS order. Use `address.AddressFamily` (NOT hardcoded `InterNetwork`). This is "Happy Eyeballs lite" — full RFC 8305 deferred to Phase 10.
     ```csharp
     Socket socket = null;
     Exception lastException = null;
     foreach (var address in addresses)
     {
         try
         {
             socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
             socket.NoDelay = true;
             // ConnectAsync has no CancellationToken in .NET Std 2.1.
             // Use dispose-on-cancel pattern for best-effort cancellation.
             using (ct.Register(() => socket.Dispose()))
             {
                 await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
             }
             lastException = null;
             break; // success
         }
         catch (Exception ex)
         {
             lastException = ex;
             socket?.Dispose();
             socket = null;
             if (ct.IsCancellationRequested)
                 throw new OperationCanceledException("Connection cancelled", ex, ct);
         }
     }
     if (socket == null)
         throw lastException ?? new SocketException((int)SocketError.HostNotFound);
     ```
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
6. Return `new ConnectionLease(this, semaphore, new PooledConnection(socket, stream, host, port, secure))`
7. On exception (before lease is created): release semaphore, rethrow:
   ```csharp
   catch
   {
       semaphore.Release(); // Permit must not leak
       throw;
   }
   ```

#### `EnqueueConnection(connection)` (internal)

Called by `ConnectionLease.ReturnToPool()`. Enqueues a live connection for reuse. Does NOT release the semaphore — that is handled by `ConnectionLease.Dispose()`.

1. If `_disposed` or connection null/dead → dispose connection, return
2. Update `connection.LastUsed`
3. Enqueue to idle pool

**Note on ordering:** Since the semaphore is released by `ConnectionLease.Dispose()` (which runs after `ReturnToPool()`), there is no race between enqueue and semaphore release — the connection is available in the queue before the permit is released.

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

**Semaphore cap:** To prevent unbounded memory growth for apps hitting many unique hosts (e.g., CDN subdomains), enforce a maximum semaphore count with basic eviction:
```csharp
private const int MaxSemaphoreEntries = 1000;

// After getting/creating semaphore in GetConnectionAsync:
if (_semaphores.Count > MaxSemaphoreEntries)
{
    // Evict idle entries (keys with no active connections and no queued waiters).
    // CRITICAL: Never evict the key we are actively using in this call.
    // CRITICAL: Drain and dispose all queued connections before removing — otherwise sockets leak.
    foreach (var kvp in _semaphores)
    {
        if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            continue; // Never evict the key currently being used

        if (kvp.Value.CurrentCount == _maxConnectionsPerHost) // No connections in use
        {
            if (_semaphores.TryRemove(kvp.Key, out var removedSemaphore))
            {
                removedSemaphore.Dispose();

                // Drain and dispose all idle connections for this key
                if (_idleConnections.TryRemove(kvp.Key, out var removedQueue))
                {
                    while (removedQueue.TryDequeue(out var conn))
                    {
                        conn.Dispose();
                    }
                }
            }
            if (_semaphores.Count <= MaxSemaphoreEntries)
                break;
        }
    }
    // NOTE: If eviction cannot bring count below MaxSemaphoreEntries (all semaphores
    // have active connections), temporarily exceeding the cap is acceptable. The
    // alternative (blocking) could deadlock. Phase 10 LRU cleanup handles this better.
}
```
**Note:** This is a basic cap, not a full LRU. Phase 10 will implement proper background idle cleanup with time-based eviction.

---

## Step 2: `TlsStreamWrapper`

**File:** `Runtime/Transport/Tls/TlsStreamWrapper.cs`

Static utility for TLS handshake:

### `WrapAsync(Stream innerStream, string host, CancellationToken ct, string[] alpnProtocols = null)`

Use `SslClientAuthenticationOptions` overload if available (provides `CancellationToken` support and ALPN). This overload was added in .NET 5.0 and is NOT part of .NET Standard 2.1 spec — Unity 2021.3 Mono may or may not expose it. **A runtime probe with fallback is required.**

```csharp
var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, ValidateServerCertificate);

// Probe for SslClientAuthenticationOptions overload availability at startup.
// Cache result in a static bool to avoid reflection on every handshake.
private static readonly bool _hasSslOptionsOverload = CheckSslOptionsOverload();

private static bool CheckSslOptionsOverload()
{
    try
    {
        // Check if the SslClientAuthenticationOptions overload exists
        var method = typeof(SslStream).GetMethod("AuthenticateAsClientAsync",
            new[] { typeof(SslClientAuthenticationOptions), typeof(CancellationToken) });
        return method != null;
    }
    catch { return false; }
}
```

**Primary path (SslClientAuthenticationOptions available):**

**IMPORTANT:** The `AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)` overload is a .NET 5+ API — it does NOT exist in .NET Standard 2.1 at compile time. A direct call will fail to compile. The primary path MUST use reflection invocation via the cached `MethodInfo`. The `SslClientAuthenticationOptions` type itself exists in .NET Standard 2.1 (`System.Net.Security` namespace), so creating the options object is safe.

```csharp
// Cache the MethodInfo at startup (one-time reflection cost)
private static readonly MethodInfo _sslOptionsMethod = typeof(SslStream).GetMethod(
    "AuthenticateAsClientAsync",
    new[] { typeof(SslClientAuthenticationOptions), typeof(CancellationToken) });

private static bool HasSslOptionsOverload => _sslOptionsMethod != null;

if (HasSslOptionsOverload)
{
    var sslOptions = new SslClientAuthenticationOptions
    {
        TargetHost = host,
        EnabledSslProtocols = SslProtocols.None  // OS negotiates best available
    };

    // ALPN negotiation — prepared for Phase 3B
    if (alpnProtocols != null && alpnProtocols.Length > 0)
    {
        sslOptions.ApplicationProtocols = new List<SslApplicationProtocol>();
        foreach (var proto in alpnProtocols)
        {
            if (proto == "h2")
                sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http2);
            else if (proto == "http/1.1")
                sslOptions.ApplicationProtocols.Add(SslApplicationProtocol.Http11);
        }
    }

    // Invoke via reflection — the overload may not exist at compile time (.NET Std 2.1)
    // Boxing overhead (new object[]) is one-time per connection — acceptable.
    var task = (Task)_sslOptionsMethod.Invoke(sslStream, new object[] { sslOptions, ct });
    await task.ConfigureAwait(false);
}
```

**Fallback path (4-arg overload, always available in .NET Standard 2.1):**
```csharp
else
{
    // 4-arg overload: no CancellationToken, no ALPN.
    // NOTE: ALPN is unavailable through this path — HTTP/2 negotiation
    // will not work until the SslClientAuthenticationOptions overload is confirmed.
    //
    // Use Task.WhenAny for cancellation instead of dispose-on-cancel pattern.
    // The dispose-on-cancel pattern has a race: if the handshake completes just as
    // the token fires, the callback disposes the stream while it's being returned.
    ct.ThrowIfCancellationRequested();

    var handshakeTask = sslStream.AuthenticateAsClientAsync(
        host,
        null,                  // clientCertificates
        SslProtocols.None,     // OS negotiates
        checkCertificateRevocation: false
    );

    var completedTask = await Task.WhenAny(handshakeTask, Task.Delay(-1, ct)).ConfigureAwait(false);

    if (completedTask != handshakeTask)
    {
        // Cancellation fired before handshake completed — dispose the stream
        sslStream.Dispose();
        ct.ThrowIfCancellationRequested(); // Throws OperationCanceledException
    }

    await handshakeTask.ConfigureAwait(false); // Propagate any handshake exceptions
}
```

**Phase 3.5 verification:** Add a Unity Editor test that logs which path was taken. If the primary path is unavailable, document ALPN as blocked until confirmed on target Unity version.

**Why `SslProtocols.None`:** Explicitly specifying `SslProtocols.Tls13` can cause `AuthenticationException` on older macOS (pre-10.15) and iOS (pre-13) where the OS does not support TLS 1.3. `SslProtocols.None` lets the OS negotiate the best available protocol. This is the Microsoft-recommended approach for cross-platform libraries.

### Post-handshake TLS minimum enforcement (REQUIRED)

With `SslProtocols.None`, the OS could negotiate TLS 1.0 or 1.1 on very old servers. Enforce a minimum of TLS 1.2 after the handshake:

```csharp
#pragma warning disable SYSLIB0039 // SslProtocol property is obsolete in .NET 7+ but needed here
if (sslStream.SslProtocol < SslProtocols.Tls12)
{
    sslStream.Dispose(); // Close connection negotiated at insecure protocol
    throw new SecurityException(
        $"Server negotiated {sslStream.SslProtocol}, but minimum TLS 1.2 is required");
}
#pragma warning restore SYSLIB0039
```

This check is **not redundant** — with `SslProtocols.None`, it is the sole enforcement of the TLS 1.2 minimum.

### `GetNegotiatedProtocol(SslStream)`

Returns `"h2"`, `"http/1.1"`, or `null` based on `sslStream.NegotiatedApplicationProtocol`. Prepared for Phase 3B.

### `ValidateServerCertificate` callback

- Returns `sslPolicyErrors == SslPolicyErrors.None`
- Logs `sslPolicyErrors` flags for diagnostics when validation fails
- **IL2CPP note:** Use a static method (no instance captures) to avoid delegate marshaling issues under IL2CPP.

### Certificate Revocation (Known Limitation)

Certificate revocation checking is disabled on all TLS paths (`checkCertificateRevocation: false` on the fallback path; default `X509RevocationMode.NoCheck` on the primary path). Revoked certificates will be accepted. This is a deliberate trade-off: CRL distribution points can be unreachable (especially on mobile networks), causing connection failures. CRL/OCSP support deferred to Phase 9 (security hardening).

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
