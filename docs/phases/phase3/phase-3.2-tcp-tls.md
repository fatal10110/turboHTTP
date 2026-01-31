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
- `LastUsed` — stored as `long _lastUsedTicks`, accessed via `Interlocked.Read(ref _lastUsedTicks)` and `Interlocked.Exchange(ref _lastUsedTicks, value)`. **C# does not allow `volatile` on `long` fields** (CS0677 — only types with atomic reads/writes by spec can be volatile, and `long` is not guaranteed atomic on 32-bit platforms). `Interlocked` provides both atomicity and memory ordering on all platforms including ARM32 IL2CPP. Tearing on `LastUsed` is non-critical (worst case: connection evicted slightly early/late), but `Interlocked` avoids the compilation error entirely.
- `IsReused` (bool) — set to `true` when a connection is dequeued from the idle pool (i.e., it was previously used and returned). New connections have `IsReused = false`. Used by `RawSocketTransport` retry-on-stale logic to distinguish fresh connections from pooled reused ones — retry is only attempted on reused connections (stale socket from server close).
- `NegotiatedTlsVersion` (SslProtocols?, nullable) — set after TLS handshake from `sslStream.SslProtocol`. Null for non-TLS connections. Allows tests to assert TLS 1.2+ was negotiated in the success case (not just failure enforcement).
- `IsAlive` — **best-effort** detection of server-closed connections. **MUST NOT be relied upon for correctness** — the retry-on-stale mechanism in `RawSocketTransport` (Phase 3.4) is the true safety net. For TLS connections, `Socket.Available` does not reflect SslStream's internal buffering — if SslStream has buffered decrypted data, `Socket.Available == 0 && Socket.Poll(0, SelectRead)` would incorrectly report the connection as dead. Also consider checking `Stream.CanRead` as an additional guard. Returns `false` after disposal (guards against `ObjectDisposedException`). **Must never be called after `Dispose()` in normal flow** — the `ConnectionLease` class ensures `ReturnToPool()` (which calls `IsAlive`) is always called before `Dispose()`.
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
    /// IsAlive check is performed OUTSIDE the lock to avoid holding the lock during Socket.Poll() syscall.
    /// </summary>
    public void ReturnToPool()
    {
        bool shouldReturn = false;
        lock (_lock)
        {
            if (!_released && !_disposed)
            {
                shouldReturn = true;
                _released = true; // Mark released INSIDE lock to prevent Dispose() from disposing
            }
        }

        if (shouldReturn)
        {
            // IsAlive check OUTSIDE lock — Socket.Poll(0, ...) is a syscall
            if (Connection.IsAlive)
                _pool.EnqueueConnection(Connection);
            else
                Connection.Dispose(); // Stale connection, discard
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
     **Note:** The background DNS task continues running after timeout (unavoidable in .NET Std 2.1). But the user-facing request fails fast instead of hanging indefinitely. If DNS completes later, the `IPAddress[]` result is GC'd. If DNS hangs indefinitely, the ThreadPool thread is blocked until OS timeout (~30s). Under pathological DNS failure scenarios (mobile network loss), there can be a buildup of background DNS tasks — this is inherent to .NET Standard 2.1's lack of cancellable DNS resolution. Consider caching DNS results in Phase 10 to amortize cost.
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
     SslProtocols? negotiatedTls = null;
     try
     {
         var tlsResult = await TlsStreamWrapper.WrapAsync(stream, host, ct).ConfigureAwait(false);
         stream = tlsResult.Stream;
         negotiatedTls = tlsResult.NegotiatedProtocol;
     }
     catch
     {
         stream.Dispose(); // Cascades to NetworkStream → Socket
         throw;
     }
     ```
6. Create connection and set TLS version:
   ```csharp
   var connection = new PooledConnection(socket, stream, host, port, secure);
   if (negotiatedTls.HasValue)
       connection.NegotiatedTlsVersion = negotiatedTls.Value;
   ```
   Return `new ConnectionLease(this, semaphore, connection)`
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

#### `GetConnectionAsync` — Dispose guard

At the top of `GetConnectionAsync`, check `_disposed` and throw:
```csharp
if (_disposed)
    throw new ObjectDisposedException(nameof(TcpConnectionPool));
```

#### `EnqueueConnection` — Dispose guard

At the top of `EnqueueConnection`, check `_disposed` and dispose the connection instead of enqueuing:
```csharp
if (_disposed || connection == null || !connection.IsAlive)
{
    connection?.Dispose();
    return;
}
```

#### `Dispose()`

1. Set `_disposed = true` (volatile bool)
2. Drain all queues, dispose each connection
3. Dispose all semaphores (safe — no new `GetConnectionAsync` calls will reference them after `_disposed` guard)
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
    // CRITICAL: Drain and dispose all queued connections BEFORE removing semaphore entry.
    // CRITICAL: Do NOT dispose semaphores during eviction — other threads may still
    //   reference them via GetOrAdd. Between the CurrentCount check and TryRemove,
    //   another thread could call GetConnectionAsync for the same key and WaitAsync on
    //   the semaphore. Disposing it would cause ObjectDisposedException.
    //   Semaphores are only disposed in TcpConnectionPool.Dispose() when all references
    //   are known to be gone. Memory cost is trivial (~100 bytes per orphaned semaphore).
    foreach (var kvp in _semaphores)
    {
        if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            continue; // Never evict the key currently being used

        if (kvp.Value.CurrentCount == _maxConnectionsPerHost) // No connections in use
        {
            // Step 1: Remove idle connections FIRST to prevent new enqueues
            if (_idleConnections.TryRemove(kvp.Key, out var removedQueue))
            {
                while (removedQueue.TryDequeue(out var conn))
                {
                    conn.Dispose();
                }
            }

            // Step 2: Remove semaphore from dictionary but do NOT dispose it
            _semaphores.TryRemove(kvp.Key, out _);

            if (_semaphores.Count <= MaxSemaphoreEntries)
                break;
        }
    }
    // NOTE: If eviction cannot bring count below MaxSemaphoreEntries (all semaphores
    // have active connections), temporarily exceeding the cap is acceptable. The
    // alternative (blocking) could deadlock. Phase 10 LRU cleanup handles this better.
}
```
**Note:** This is a basic cap, not a full LRU. Semaphores are NOT disposed during eviction (race-safe). Phase 10 will implement proper background idle cleanup with time-based eviction and quiescence checks for safe semaphore disposal.

---

## Step 2: `TlsStreamWrapper`

**File:** `Runtime/Transport/Tls/TlsStreamWrapper.cs`

Static utility for TLS handshake:

### `TlsResult` (return type)

`WrapAsync` returns a struct containing both the wrapped stream and the negotiated TLS version:

```csharp
internal readonly struct TlsResult
{
    public Stream Stream { get; }
    public SslProtocols NegotiatedProtocol { get; }

    public TlsResult(Stream stream, SslProtocols negotiatedProtocol)
    {
        Stream = stream;
        NegotiatedProtocol = negotiatedProtocol;
    }
}
```

This allows `TcpConnectionPool` to set `PooledConnection.NegotiatedTlsVersion` from the result without needing access to the `SslStream` instance (which is hidden behind the `Stream` type).

### `WrapAsync(Stream innerStream, string host, CancellationToken ct, string[] alpnProtocols = null)` → returns `TlsResult`

Use `SslClientAuthenticationOptions` overload if available (provides `CancellationToken` support and ALPN). This overload was added in .NET 5.0 and is NOT part of .NET Standard 2.1 spec — Unity 2021.3 Mono may or may not expose it. **A runtime probe with fallback is required.**

```csharp
var sslStream = new SslStream(innerStream, leaveInnerStreamOpen: false, ValidateServerCertificate);

// Single cached MethodInfo probe for SslClientAuthenticationOptions overload.
// One-time reflection cost at startup. MethodInfo is null if overload is unavailable.
// NOTE: The previous plan had a redundant `_hasSslOptionsOverload` bool field — removed.
//       Only one probe mechanism is needed: the cached MethodInfo itself.
private static readonly MethodInfo _sslOptionsMethod = typeof(SslStream).GetMethod(
    "AuthenticateAsClientAsync",
    new[] { typeof(SslClientAuthenticationOptions), typeof(CancellationToken) });

private static bool HasSslOptionsOverload => _sslOptionsMethod != null;
```

**Primary path (SslClientAuthenticationOptions available):**

**IMPORTANT:** The `AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)` overload is a .NET 5+ API — it does NOT exist in .NET Standard 2.1 at compile time. A direct call will fail to compile. The primary path MUST use reflection invocation via the cached `MethodInfo`. The `SslClientAuthenticationOptions` type itself exists in .NET Standard 2.1 (`System.Net.Security` namespace), so creating the options object is safe.

```csharp

if (HasSslOptionsOverload)
{
    var sslOptions = new SslClientAuthenticationOptions
    {
        TargetHost = host,
        EnabledSslProtocols = SslProtocols.None,  // OS negotiates best available
        CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
        // Explicitly disable CRL/OCSP to match fallback path behavior.
        // Without this, the default may differ per platform (Unity's Mono vs IL2CPP,
        // macOS vs iOS), causing inconsistent revocation checking behavior.
        // See Certificate Revocation (Known Limitation) section below.
    };

    // ALPN negotiation — prepared for Phase 3B.
    // COMPILE-TIME GUARD: SslClientAuthenticationOptions.ApplicationProtocols property
    // and SslApplicationProtocol struct are .NET 5+ additions — they do NOT exist in
    // .NET Standard 2.1 at compile time. Direct use will cause CS0117/CS0246.
    // Must use reflection to set ALPN, similar to how the overload itself is invoked.
    if (alpnProtocols != null && alpnProtocols.Length > 0)
    {
        SetAlpnViaReflection(sslOptions, alpnProtocols);
    }
    // ...
    // Helper method (called only when ALPN protocols are requested):
    // private static readonly PropertyInfo _applicationProtocolsProp =
    //     typeof(SslClientAuthenticationOptions).GetProperty("ApplicationProtocols");
    // private static readonly Type _sslAppProtocolType =
    //     Type.GetType("System.Net.Security.SslApplicationProtocol, System.Net.Security");
    //
    // private static void SetAlpnViaReflection(SslClientAuthenticationOptions options, string[] protocols)
    // {
    //     if (_applicationProtocolsProp == null || _sslAppProtocolType == null)
    //         return; // ALPN not available on this platform — silently skip
    //
    //     // Create List<SslApplicationProtocol> via reflection
    //     var listType = typeof(List<>).MakeGenericType(_sslAppProtocolType);
    //     var list = Activator.CreateInstance(listType);
    //     var addMethod = listType.GetMethod("Add");
    //
    //     // SslApplicationProtocol has static fields Http2 and Http11
    //     foreach (var proto in protocols)
    //     {
    //         var fieldName = proto == "h2" ? "Http2" : proto == "http/1.1" ? "Http11" : null;
    //         if (fieldName == null) continue;
    //         var field = _sslAppProtocolType.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
    //         if (field != null)
    //             addMethod.Invoke(list, new[] { field.GetValue(null) });
    //     }
    //
    //     _applicationProtocolsProp.SetValue(options, list);
    // }
    //
    // NOTE: If SslApplicationProtocol type is not found (Unity 2021.3 may not have it),
    // the method silently returns — ALPN is simply not negotiated. This is safe for
    // HTTP/1.1 (Phase 3). Phase 3B (HTTP/2) must verify ALPN availability as a gate.

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

### Post-handshake: TLS version surfacing + minimum enforcement (REQUIRED)

After a successful handshake (both primary and fallback paths), the `sslStream` variable is in scope and accessible. Enforce TLS 1.2 minimum and capture the negotiated version:

```csharp
// Post-handshake check — sslStream is in scope from the top of WrapAsync
#pragma warning disable SYSLIB0039 // SslProtocol property is obsolete in .NET 7+ but needed here
var negotiatedProtocol = sslStream.SslProtocol;
#pragma warning restore SYSLIB0039

if (negotiatedProtocol < SslProtocols.Tls12)
{
    sslStream.Dispose(); // Close connection negotiated at insecure protocol
    throw new AuthenticationException(
        $"Server negotiated {negotiatedProtocol}, but minimum TLS 1.2 is required");
}

return new TlsResult(sslStream, negotiatedProtocol);
```

**Why `AuthenticationException` (not `SecurityException`):** The exception must be caught by `RawSocketTransport`'s exception mapping (Phase 3.4). `AuthenticationException` is already caught and mapped to `UHttpErrorType.CertificateError`. `SecurityException` is NOT caught — it would fall through to the generic `Exception` handler and be incorrectly reported as `Unknown`. Using `AuthenticationException` also aligns semantically: TLS version negotiation is part of the authentication handshake.

**Caller-side (in `TcpConnectionPool.GetConnectionAsync`):**
```csharp
var tlsResult = await TlsStreamWrapper.WrapAsync(stream, host, ct).ConfigureAwait(false);
stream = tlsResult.Stream;
// Set on connection after construction:
connection.NegotiatedTlsVersion = tlsResult.NegotiatedProtocol;
```

This check is **not redundant** — with `SslProtocols.None`, it is the sole enforcement of the TLS 1.2 minimum.

### `GetNegotiatedProtocol(SslStream)`

Returns `"h2"`, `"http/1.1"`, or `null`. Prepared for Phase 3B. **Must use reflection** to access `sslStream.NegotiatedApplicationProtocol` — this property is .NET 5+ and may not exist at compile time in Unity 2021.3's .NET Standard 2.1 profile. If the property is unavailable, returns `null` (ALPN not negotiated).

### `ValidateServerCertificate` callback

- Returns `sslPolicyErrors == SslPolicyErrors.None`
- Logs `sslPolicyErrors` flags for diagnostics when validation fails
- **IL2CPP note:** Use a static method (no instance captures) to avoid delegate marshaling issues under IL2CPP.

### Certificate Revocation (Known Limitation)

Certificate revocation checking is disabled on all TLS paths (`checkCertificateRevocation: false` on the fallback path; default `X509RevocationMode.NoCheck` on the primary path). Revoked certificates will be accepted. This is a deliberate trade-off: CRL distribution points can be unreachable (especially on mobile networks), causing connection failures. CRL/OCSP support deferred to Phase 9 (security hardening).

### IL2CPP Stripping Protection (`link.xml`)

**File:** `Runtime/Transport/link.xml`

The reflection probe for `SslStream.AuthenticateAsClientAsync(SslClientAuthenticationOptions, CancellationToken)` uses `typeof(SslStream).GetMethod(...)`. Under IL2CPP managed code stripping, unreferenced overloads may be stripped, causing the `MethodInfo` to be `null` even when the overload exists in the BCL. A `link.xml` file preserves the necessary types:

```xml
<linker>
  <assembly fullname="System.Net.Security">
    <type fullname="System.Net.Security.SslStream" preserve="all"/>
    <type fullname="System.Net.Security.SslClientAuthenticationOptions" preserve="all"/>
  </assembly>
  <assembly fullname="System.Text.Encoding.CodePages">
    <type fullname="System.Text.Encoding.CodePages.*" preserve="all"/>
  </assembly>
</linker>
```

This also preserves codepage encodings for `Encoding.GetEncoding(28591)` (Latin-1). Place in `Runtime/Transport/` — Unity's build pipeline automatically discovers `link.xml` files in any directory.

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
