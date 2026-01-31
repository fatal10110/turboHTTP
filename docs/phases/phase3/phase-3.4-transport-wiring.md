# Phase 3.4: RawSocketTransport & Wiring

**Depends on:** 3.1 (Client API), 3.2 (TCP/TLS), 3.3 (HTTP/1.1 Protocol)
**Assembly:** `TurboHTTP.Transport`
**Files to create:** 3 new (RawSocketTransport.cs + TransportModuleInitializer.cs + ModuleInitializerAttribute.cs polyfill)

---

## Step 1: `RawSocketTransport`

**File:** `Runtime/Transport/RawSocketTransport.cs`

Default `IHttpTransport` implementation that wires together the connection pool, TLS, serializer, and parser.

### Transport Registration (Bootstrap Problem)

The static constructor approach has a **circular dependency**: `HttpTransportFactory.Default` calls `_factory()`, but `_factory` is set by `RawSocketTransport`'s static constructor, which only runs when `RawSocketTransport` is first referenced. Nothing references it before `Default` is called — the factory is null and throws.

**Solution: C# 9 Module Initializer** (supported in Unity 2021.3 LTS with .NET Standard 2.1)

Place the module initializer in the `TurboHTTP.Transport` assembly itself. It auto-runs when the assembly is loaded — no Unity-specific bootstrap assembly needed. This keeps `TurboHTTP.Unity` optional and Core-only, preserving the module architecture.

**ModuleInitializerAttribute polyfill (REQUIRED):**

Unity 2021.3 LTS targets .NET Standard 2.1, which does NOT include `System.Runtime.CompilerServices.ModuleInitializerAttribute` (introduced in .NET 5.0). The C# 9 compiler supports `[ModuleInitializer]` syntax, but the attribute type must exist at compile time. Without a polyfill, compilation fails with `CS0246`.

```csharp
// File: Runtime/Transport/Internal/ModuleInitializerAttribute.cs
// Polyfill for .NET Standard 2.1 — Unity 2021.3 BCL does not include this attribute.
// The C# compiler recognizes this attribute for module initializer codegen regardless of
// where it is defined. Using `internal` prevents conflicts if Unity adds it in future versions.
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class ModuleInitializerAttribute : Attribute { }
}
#endif
```

**Module initializer registration:**

```csharp
// File: Runtime/Transport/TransportModuleInitializer.cs
using System.Runtime.CompilerServices;

namespace TurboHTTP.Transport
{
    internal static class TransportModuleInitializer
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            HttpTransportFactory.Register(() => new RawSocketTransport());
        }
    }
}
```

**How it works:**
- `[ModuleInitializer]` methods run automatically when the assembly is first loaded by the runtime
- Since `TurboHTTP.Transport` is referenced by any code that uses the HTTP client (directly or transitively), the registration happens before `HttpTransportFactory.Default` is first accessed
- No changes to `TurboHTTP.Unity.asmdef` needed — it stays `autoReferenced: false` and references only Core

**IL2CPP Compatibility Note:** `[ModuleInitializer]` compiles to a `.cctor` (module constructor) in IL metadata. Under Mono (Unity Editor, standalone), this works reliably. Under IL2CPP, all assemblies are statically linked — "assembly loading" semantics differ. IL2CPP runs module initializers before any type in that assembly is used, but the timing relative to other assemblies' static constructors is implementation-defined. If `HttpTransportFactory.Default` is accessed before IL2CPP has triggered the Transport assembly's module initializer, it will throw `InvalidOperationException` with a message directing users to call `RawSocketTransport.EnsureRegistered()`.

**Phase 3.5 mandatory gate:** An IL2CPP build test must verify the module initializer fires correctly. If it does not, the documented fallback (`EnsureRegistered()`) must be promoted to the primary registration mechanism in documentation.

**Fallback for non-Unity contexts or IL2CPP issues:** Users call `RawSocketTransport.EnsureRegistered()` at startup, or `HttpTransportFactory.Register(() => new RawSocketTransport())` directly.

**Explicit registration method (kept for testability):**
```csharp
// In RawSocketTransport.cs:
/// <summary>
/// Explicitly register this transport with HttpTransportFactory.
/// Not needed in normal usage (module initializer handles it).
/// Useful for tests that call HttpTransportFactory.Reset().
/// </summary>
public static void EnsureRegistered()
{
    HttpTransportFactory.Register(() => new RawSocketTransport());
}
```

**Note:** The `TurboHTTP.Unity` module is NOT used for bootstrap. It remains optional and will be used in later phases for Unity-specific functionality (main thread dispatcher, coroutine helpers, etc.).

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

4. Get connection lease (semaphore permit is now owned by the lease):
   context.RecordEvent("TransportConnecting")
   using var lease = await _pool.GetConnectionAsync(host, port, secure, ct)
   // ConnectionLease.Dispose() ALWAYS releases the semaphore permit,
   // whether the connection is returned to pool or discarded.

5. Try serialize + parse:
   context.RecordEvent("TransportSending")
   await Http11RequestSerializer.SerializeAsync(request, lease.Connection.Stream, ct)

   context.RecordEvent("TransportReceiving")
   parsed = await Http11ResponseParser.ParseAsync(lease.Connection.Stream, request.Method, ct)

   context.RecordEvent("TransportComplete")

6. Connection return (keep-alive):
   if (parsed.KeepAlive)
   {
       lease.ReturnToPool();
       // Connection is now in the idle queue. When lease.Dispose() runs,
       // it sees _released=true and only releases the semaphore (no dispose).
   }
   // If NOT keep-alive: lease.Dispose() will dispose the connection AND release semaphore.
   // If an exception occurred: lease.Dispose() will dispose the connection AND release semaphore.
   // NO PERMIT LEAK IS POSSIBLE.

7. Return UHttpResponse with body intact (even for 4xx/5xx)

// No finally block needed — `using var lease` handles all cleanup.
```

### Retry-on-Stale Connection

If the first write to a reused connection throws `IOException`:
1. Dispose the stale lease (`lease.Dispose()` — releases semaphore + disposes connection). **Note:** The original `using var lease` will also call `Dispose()` at method exit, but `ConnectionLease.Dispose()` is idempotent (second call is a no-op).
2. Get a fresh lease: `using var freshLease = await _pool.GetConnectionAsync(host, port, secure, ct)`
3. Retry the serialize + parse once using `freshLease.Connection.Stream`
4. If it fails again, let the exception propagate (freshLease.Dispose() still runs, releasing semaphore)

This handles the common case where a server closed an idle keep-alive connection. The `ConnectionLease` pattern ensures semaphore permits are never leaked, even during retry.

**Semaphore re-acquisition note:** Between disposing the stale lease (step 1, releases semaphore) and acquiring a fresh lease (step 2, acquires semaphore), the permit is released back to the pool. Under high concurrency with `maxConnectionsPerHost` other requests waiting, the retry request competes with all waiters for the permit. This can cause unexpected latency spikes for the retrying request. This is technically correct but worth documenting. A permit-preserving retry path (creating a fresh connection without going through the semaphore) is deferred to Phase 10 as an optimization.

### Exception Mapping (throws, does NOT return error responses)

```csharp
catch (UHttpException)
{
    // Already mapped by pool/TLS layer (e.g., DNS timeout) — pass through, do NOT re-wrap.
    throw;
}
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
2. `TransportModuleInitializer.cs` compiles in `TurboHTTP.Transport` assembly (no new assembly def needed)
3. When `TurboHTTP.Transport` assembly loads, `[ModuleInitializer]` auto-registers transport. `HttpTransportFactory.Default` returns `RawSocketTransport` instance without explicit bootstrap.
4. Timeout enforcement works (linked CancellationTokenSource)
5. Retry-on-stale: IOException on first write → retry with fresh connection
6. Exception mapping correctly distinguishes timeout / cancelled / network / TLS / unknown
7. Connection returned to pool on keep-alive, disposed otherwise — via `ConnectionLease` pattern
8. Semaphore permit ALWAYS released (no leak on any code path: keep-alive, non-keepalive, exception, timeout, cancellation)
9. No double-dispose — `ConnectionLease._released` flag prevents dispose after `ReturnToPool()`
