# Phase 3.4: RawSocketTransport & Wiring

**Depends on:** 3.1 (Client API), 3.2 (TCP/TLS), 3.3 (HTTP/1.1 Protocol)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 1 new

---

## Step 1: `RawSocketTransport`

**File:** `Runtime/Transport/RawSocketTransport.cs`

Default `IHttpTransport` implementation that wires together the connection pool, TLS, serializer, and parser.

### Transport Registration (Bootstrap Problem)

The static constructor approach has a **circular dependency**: `HttpTransportFactory.Default` calls `_factory()`, but `_factory` is set by `RawSocketTransport`'s static constructor, which only runs when `RawSocketTransport` is first referenced. Nothing references it before `Default` is called — the factory is null and throws.

Since Transport assembly has `noEngineReferences: true`, `[RuntimeInitializeOnLoadMethod]` cannot be used directly.

**Solution:** Provide both a static constructor (for direct usage) AND an explicit registration method. The `TurboHTTP.Unity` bootstrap module (already planned in the module system) will handle automatic registration:

```csharp
// In RawSocketTransport.cs:
static RawSocketTransport()
{
    HttpTransportFactory.Register(() => new RawSocketTransport());
}

/// <summary>
/// Call to ensure the static constructor has run and transport is registered.
/// Typically called by the TurboHTTP.Unity bootstrap module.
/// </summary>
public static void EnsureRegistered() { /* Static constructor does the work */ }
```

```csharp
// In a separate TurboHTTP.Unity assembly (autoReferenced: true, references Core + Transport):
// File: Runtime/Unity/TransportBootstrap.cs
namespace TurboHTTP.Unity
{
    internal static class TransportBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Initialize()
        {
            RawSocketTransport.EnsureRegistered();
        }
    }
}
```

**Fallback for non-Unity contexts (tests, manual setup):** Users can call `RawSocketTransport.EnsureRegistered()` explicitly or set `HttpTransportFactory.Default = new RawSocketTransport()` directly.

**Note:** The `TurboHTTP.Unity` module is already part of the planned module system. This bootstrap class is its first file — it will grow in later phases (main thread dispatcher, coroutine helpers, etc.).

### Constructor

```csharp
public RawSocketTransport(TcpConnectionPool pool = null)
{
    _pool = pool ?? new TcpConnectionPool();
}
```

### `SendAsync(UHttpRequest request, RequestContext context, CancellationToken cancellationToken)`

All awaits use `.ConfigureAwait(false)`.

Required using: `using System.Security.Authentication;` for `AuthenticationException`.

```
1. Create timeout enforcement:
   using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
   timeoutCts.CancelAfter(request.Timeout);
   var ct = timeoutCts.Token;

2. Extract and validate connection params:
   scheme = request.Uri.Scheme.ToLowerInvariant()
   if (scheme != "http" && scheme != "https")
       throw new UHttpException(new UHttpError(UHttpErrorType.InvalidRequest,
           $"Unsupported URI scheme: {scheme}. Only http and https are supported."))
   host = request.Uri.Host
   port = request.Uri.Port (default 443 for https, 80 for http)
   secure = scheme == "https"

3. context.RecordEvent("TransportStart")

4. Get connection (with retry-on-stale):
   context.RecordEvent("TransportConnecting")
   connection = await _pool.GetConnectionAsync(host, port, secure, ct)

5. Try serialize + parse:
   context.RecordEvent("TransportSending")
   await Http11RequestSerializer.SerializeAsync(request, connection.Stream, ct)

   context.RecordEvent("TransportReceiving")
   parsed = await Http11ResponseParser.ParseAsync(connection.Stream, request.Method, ct)

   context.RecordEvent("TransportComplete")

6. Connection return (with safety flag):
   bool connectionReturned = false;
   if (parsed.KeepAlive)
   {
       _pool.ReturnConnection(connection);
       connectionReturned = true;
   }

7. Return UHttpResponse with body intact (even for 4xx/5xx)

finally:
   if (!connectionReturned) connection?.Dispose()
```

### Retry-on-Stale Connection

If the first write to a reused connection throws `IOException`:
1. Dispose the stale connection
2. Get a fresh connection from the pool
3. Retry the serialize + parse once
4. If it fails again, let the exception propagate

This handles the common case where a server closed an idle keep-alive connection.

### Exception Mapping (throws, does NOT return error responses)

```csharp
catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
{
    // Timeout fired, not user cancellation
    throw new UHttpException(new UHttpError(UHttpErrorType.Timeout,
        $"Request timed out after {request.Timeout.TotalSeconds}s"));
}
catch (OperationCanceledException)
{
    // User cancellation
    throw new UHttpException(new UHttpError(UHttpErrorType.Cancelled, "Request was cancelled"));
}
catch (IOException ex)
{
    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));
}
catch (SocketException ex)
{
    throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));
}
catch (AuthenticationException ex)
{
    throw new UHttpException(new UHttpError(UHttpErrorType.CertificateError, ex.Message, ex));
}
catch (Exception ex)
{
    throw new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex.Message, ex));
}
```

The key distinction: **timeout vs user cancellation** is determined by checking which CancellationToken fired.

### `Dispose()`

```csharp
public void Dispose()
{
    _pool?.Dispose();
}
```

---

## Verification

1. `RawSocketTransport.cs` compiles in `TurboHTTP.Transport` assembly
2. `TransportBootstrap.cs` compiles in `TurboHTTP.Unity` assembly (new assembly def needed)
3. After `TransportBootstrap.Initialize()` (or `RawSocketTransport.EnsureRegistered()`), `HttpTransportFactory.Default` returns `RawSocketTransport` instance
4. Timeout enforcement works (linked CancellationTokenSource)
5. Retry-on-stale: IOException on first write → retry with fresh connection
6. Exception mapping correctly distinguishes timeout / cancelled / network / TLS / unknown
7. Connection returned to pool on keep-alive, disposed otherwise
8. No double-dispose via `connectionReturned` flag
