# Phase 22c.1: `Http2ConnectionManager` Key Extension

**Depends on:** Phase 22a (complete)
**Assemblies:** `TurboHTTP.Transport`
**Files modified:** 1

---

## Context

`Http2ConnectionManager` currently uses `$"{host}:{port}"` as the connection key. All operations (`GetIfExists`, `GetOrCreateAsync`, `Remove`, `HasConnection`) take `(string host, int port)`. This works for direct connections where each key maps to one unique TCP + TLS connection.

For proxy-tunneled connections, the same origin can be reached through different proxies — each a different physical connection. This sub-phase adds proxy-aware overloads using a compound key `"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}"`. The `|via|` separator is collision-free because `|` is not valid in RFC 952 hostnames.

**Direct-connection methods are not modified.** They remain the hot path with no added overhead.

---

## Step 1: `BuildTunnelKey` Private Static Helper

**File:** `Runtime/Transport/Http2/Http2ConnectionManager.cs`

Add at the bottom of the class body (after existing private helpers):

```csharp
private static string BuildTunnelKey(string originHost, int originPort, string proxyHost, int proxyPort)
{
    return $"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}";
}
```

**Why `|via|`:** `|` is not valid in RFC 952/1123 hostnames and will never appear in legitimate `host:port` strings. No ambiguity between a direct key `"api.example.com:443"` and a compound key `"api.example.com:443|via|proxy.corp.com:8080"`.

---

## Step 2: `GetIfExists` Proxy-Tunneled Overload

```csharp
/// <summary>
/// Returns an existing alive HTTP/2 connection through a CONNECT proxy tunnel, or null.
/// </summary>
/// <param name="originHost">The target origin hostname (not the proxy).</param>
/// <param name="originPort">The target origin port.</param>
/// <param name="proxyHost">The proxy hostname used to reach the origin.</param>
/// <param name="proxyPort">The proxy port.</param>
public Http2Connection GetIfExists(
    string originHost, int originPort,
    string proxyHost, int proxyPort)
{
    if (Volatile.Read(ref _disposed) != 0)
        return null;

    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
    if (_connections.TryGetValue(key, out var conn) && conn.IsAlive)
        return conn;
    return null;
}
```

This mirrors the existing `GetIfExists(string host, int port)` exactly — same `_disposed` guard, same `IsAlive` check, same null-return semantics.

---

## Step 3: `GetOrCreateAsync` Proxy-Tunneled Overload

```csharp
/// <summary>
/// Gets or creates an HTTP/2 connection through a CONNECT proxy tunnel.
/// The <paramref name="tlsStream"/> must be the TLS-wrapped tunnel stream with
/// ALPN "h2" already negotiated. Ownership is transferred — caller must not
/// dispose <paramref name="tlsStream"/> after this call.
/// </summary>
public ValueTask<Http2Connection> GetOrCreateAsync(
    string originHost, int originPort,
    string proxyHost, int proxyPort,
    Stream tlsStream,
    CancellationToken ct)
{
    if (Volatile.Read(ref _disposed) != 0)
    {
        tlsStream?.Dispose();
        throw new ObjectDisposedException(nameof(Http2ConnectionManager));
    }

    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);

    // Fast path: existing alive tunnel connection — dispose caller's stream (not needed)
    if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
    {
        tlsStream.Dispose();
        return new ValueTask<Http2Connection>(existing);
    }

    return GetOrCreateCoreAsync(key, originHost, originPort, tlsStream, ct);
}
```

`GetOrCreateCoreAsync` is the extracted private implementation shared with the direct-connection overload. See Step 6.

**Fast path required:** Omitting the `TryGetValue` fast path and calling `GetOrCreateCoreAsync`
directly would cause every concurrent-reuse invocation to allocate a `Task` state machine and
acquire a `SemaphoreSlim`. The fast path here mirrors lines 70–74 of the existing direct
`GetOrCreateAsync`.

**`originHost` / `originPort`:** The `Http2Connection` constructor takes the origin host and port
(not the proxy coordinates) — these are passed through to the core helper.

---

## Step 4: `Remove` Proxy-Tunneled Overload

```csharp
/// <summary>
/// Removes and disposes a stale HTTP/2 connection through a CONNECT proxy tunnel.
/// No-op if the key is not present.
/// </summary>
public void Remove(
    string originHost, int originPort,
    string proxyHost, int proxyPort)
{
    if (Volatile.Read(ref _disposed) != 0)
        return;

    string key = BuildTunnelKey(originHost, originPort, proxyHost, proxyPort);
    if (_connections.TryRemove(key, out var conn))
        conn.Dispose();
}
```

---

## Step 5: `HasConnection` Proxy-Tunneled Overload

```csharp
/// <summary>
/// Returns true if an alive HTTP/2 connection exists for this origin through the given proxy tunnel.
/// Internal — for diagnostics and test assertions only.
/// </summary>
internal bool HasConnection(
    string originHost, int originPort,
    string proxyHost, int proxyPort)
{
    return GetIfExists(originHost, originPort, proxyHost, proxyPort) != null;
}
```

---

## Step 6: Extract `GetOrCreateCoreAsync` (Refactor)

The existing `GetOrCreateAsync(string host, int port, Stream tlsStream, CancellationToken ct)`
delegates to `GetOrCreateSlowAsync(key, host, port, tlsStream, ct)`. `GetOrCreateSlowAsync` uses
`host` and `port` to construct `new Http2Connection(tlsStream, host, port, _options,
_streamingOptions)`. The extracted core method must therefore carry `host` and `port` in addition
to the pre-built key:

```csharp
// Existing public method — update to delegate to GetOrCreateCoreAsync
public ValueTask<Http2Connection> GetOrCreateAsync(
    string host, int port, Stream tlsStream, CancellationToken ct)
{
    if (Volatile.Read(ref _disposed) != 0)
    {
        tlsStream?.Dispose();
        throw new ObjectDisposedException(nameof(Http2ConnectionManager));
    }

    string key = $"{host}:{port}";

    // Fast path: existing alive connection
    if (_connections.TryGetValue(key, out var existing) && existing.IsAlive)
    {
        tlsStream.Dispose();
        return new ValueTask<Http2Connection>(existing);
    }

    return GetOrCreateCoreAsync(key, host, port, tlsStream, ct);
}

// New private core — takes the pre-built key and the host/port for Http2Connection construction.
// For direct connections: host = origin host, port = origin port.
// For tunnel connections: host = originHost, port = originPort (not proxy coordinates).
private ValueTask<Http2Connection> GetOrCreateCoreAsync(
    string key, string host, int port, Stream tlsStream, CancellationToken ct)
{
    // Move the GetOrCreateSlowAsync body here, replacing the
    // $"{host}:{port}" key construction with the `key` parameter.
    // The host and port parameters still go to the Http2Connection constructor.
    return new ValueTask<Http2Connection>(GetOrCreateSlowAsync(key, host, port, tlsStream, ct));
}
```

**Key point:** For tunnel connections the `Http2Connection` is constructed with `originHost` and
`originPort`, not the proxy coordinates. The proxy coordinates only appear in the key string via
`BuildTunnelKey`. This matters for SNI, stream-level errors, and any future host-based logic
inside `Http2Connection`.

**[Critical] `tlsStream` leak on `WaitAsync` cancellation:** In `GetOrCreateSlowAsync`, if
`initLock.WaitAsync(ct)` throws `OperationCanceledException` before entering the `try` body,
`tlsStream` has been passed in but is not yet owned by an `Http2Connection`. The existing slow-path
`try/finally` only covers from inside the lock; it does not dispose `tlsStream` on a pre-lock
cancellation throw. For the proxy overload this is more severe than the direct-connection path
because `TransferOwnership()` has already been called — the proxy TCP socket is not pool-managed
and will never be returned to the pool.

Fix: add a `try/catch` wrapping `GetOrCreateSlowAsync` or wrap the `WaitAsync` line so `tlsStream`
is disposed if the slow path exits without constructing an `Http2Connection`. The simplest
correction is to restructure `GetOrCreateSlowAsync` to wrap the entire body in a `try/catch`
that disposes `tlsStream` on any exception exit before the `Http2Connection` constructor is called:

```csharp
private async Task<Http2Connection> GetOrCreateSlowAsync(
    string key, string host, int port, Stream tlsStream, CancellationToken ct)
{
    try
    {
        var initLock = _initLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await initLock.WaitAsync(ct);   // ← cancellation here leaks tlsStream without the outer try
        try
        {
            // ... existing body ...
        }
        finally
        {
            initLock.Release();
        }
    }
    catch
    {
        // If we exit without having constructed an Http2Connection that owns tlsStream,
        // dispose it here. Use a flag to track whether ownership was transferred.
        // Alternatively: dispose tlsStream before re-throwing if conn was never assigned.
        throw;
    }
}
```

The exact implementation pattern (flag, null-guard, or restructure) is left to the implementer.
The invariant: `tlsStream` must either be owned by an `Http2Connection` on success, or be disposed
on any exception exit from `GetOrCreateSlowAsync`.

---

## Step 7: Thread-Safety Verification

The `_initLocks` mechanism (or equivalent) used to serialize concurrent `GetOrCreateAsync` calls on the same key must work correctly with compound keys. Verify:

1. `_initLocks` is keyed by the same string key used in `_connections` — if so, compound keys work automatically.
2. Two concurrent calls with key `"api:443|via|proxy:8080"` serialize correctly (only one `Http2Connection` created).
3. A call with key `"api:443"` and a call with key `"api:443|via|proxy:8080"` do not contend on the same init lock.

---

## Completion Criteria

- [ ] `BuildTunnelKey` returns `"{originHost}:{originPort}|via|{proxyHost}:{proxyPort}"` format
- [ ] All four proxy-tunneled overloads present and delegating to shared core via compound key
- [ ] Direct-connection overloads (`GetIfExists(host, port)`, `GetOrCreateAsync(host, port, ...)`, `Remove(host, port)`, `HasConnection(host, port)`) are byte-for-byte unchanged in behavior
- [ ] `GetOrCreateCoreAsync` refactor does not change behavior for the direct-connection path
- [ ] Thread-safety: compound key operations use the same `_initLocks` (or equivalent) as direct keys

### Unit Tests

All tests go in `Tests/Runtime/Transport/Http2/Http2ConnectionManagerTests.cs` (extend existing suite).

- [ ] Direct key `"api:443"` and tunnel key `"api:443|via|proxy:8080"` are independent entries — adding one does not affect the other
- [ ] Two different proxies to the same origin produce independent entries (`|via|proxy-a:8080` ≠ `|via|proxy-b:3128`)
- [ ] `GetIfExists(origin, port, proxy, pport)` returns null when only the direct key `"origin:port"` exists
- [ ] `GetIfExists(origin, port)` returns null when only the tunnel key exists
- [ ] `Remove(origin, port, proxy, pport)` does not affect the direct key entry for the same origin
- [ ] `GetOrCreateAsync(origin, port, proxy, pport, stream, ct)` creates a connection under the compound key, independent of any direct-key connection
- [ ] Concurrent calls with the same compound key produce exactly one `Http2Connection` (init lock serialization)
- [ ] Concurrent calls with `"api:443"` and `"api:443|via|proxy:8080"` do not contend — both proceed without blocking each other

---

## Notes for Implementation

- Read the current `Http2ConnectionManager.cs` implementation before writing any code. The `_initLocks` structure and `GetOrCreateSlowAsync` internals determine the exact refactoring required.
- Do not change visibility of any existing methods.
- Do not add XML doc to methods that don't already have it, except for the new public/internal overloads added here.
