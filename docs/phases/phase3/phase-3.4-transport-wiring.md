# Phase 3.4: RawSocketTransport & Wiring

**Depends on:** 3.1 (Client API), 3.2 (TCP/TLS), 3.3 (HTTP/1.1 Protocol)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 1 new

---

## Step 1: `RawSocketTransport`

**File:** `Runtime/Transport/RawSocketTransport.cs`

Default `IHttpTransport` implementation that wires together the connection pool, TLS, serializer, and parser.

### Static Constructor (Transport Registration)

```csharp
static RawSocketTransport()
{
    HttpTransportFactory.Register(() => new RawSocketTransport());
}
```

Since Transport assembly has `noEngineReferences: true`, we cannot use `[RuntimeInitializeOnLoadMethod]`. The static constructor runs when `RawSocketTransport` is first referenced — which happens when `HttpTransportFactory.Default` getter calls the registered factory.

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

2. Extract connection params:
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

1. File compiles in `TurboHTTP.Transport` assembly
2. Static constructor registers factory with `HttpTransportFactory`
3. `HttpTransportFactory.Default` returns `RawSocketTransport` instance
4. Timeout enforcement works (linked CancellationTokenSource)
5. Retry-on-stale: IOException on first write → retry with fresh connection
6. Exception mapping correctly distinguishes timeout / cancelled / network / TLS / unknown
7. Connection returned to pool on keep-alive, disposed otherwise
8. No double-dispose via `connectionReturned` flag
