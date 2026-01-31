# Phase 3.1: Client API (Core Assembly)

**Depends on:** Phase 2 (complete)
**Assembly:** `TurboHTTP.Core`
**Files to create:** 4 new, 1 modified

---

## Step 1: `IHttpMiddleware` interface stub

**File:** `Runtime/Core/IHttpMiddleware.cs`

Stub for Phase 4. Required now because `UHttpClientOptions.Middlewares` references it.

```csharp
namespace TurboHTTP.Core
{
    public delegate Task<UHttpResponse> HttpPipelineDelegate(
        UHttpRequest request, RequestContext context, CancellationToken cancellationToken);

    public interface IHttpMiddleware
    {
        Task<UHttpResponse> InvokeAsync(
            UHttpRequest request, RequestContext context,
            HttpPipelineDelegate next, CancellationToken cancellationToken);
    }
}
```

---

## Step 2: `UHttpClientOptions`

**File:** `Runtime/Core/UHttpClientOptions.cs`

Properties:
- `BaseUrl` (string) — for relative URL resolution
- `DefaultTimeout` (TimeSpan, default 30s)
- `DefaultHeaders` (HttpHeaders)
- `Transport` (IHttpTransport, nullable — falls back to `HttpTransportFactory.Default`)
- `Middlewares` (List<IHttpMiddleware>) — Phase 4 placeholder
- `FollowRedirects` (bool, default true) — **NOT enforced; document as Phase 4**
- `MaxRedirects` (int, default 10) — **NOT enforced; document as Phase 4**
- `DisposeTransport` (bool, default false) — when `true` and `Transport` is set, the client will dispose the transport on `UHttpClient.Dispose()`. Has no effect when using the factory-provided default transport (singleton, never disposed by clients).
- `Clone()` — deep-copies headers/middleware list; **Transport is a shared reference** (NOT snapshotted). **Middleware instances are also shared references** (typically stateless services — mutable middleware shared across cloned options may have thread-safety issues). Documented in XML comment: "Users must not mutate or dispose a Transport instance passed to UHttpClientOptions after constructing a client that uses those options."

---

## Step 3: `UHttpRequestBuilder`

**File:** `Runtime/Core/UHttpRequestBuilder.cs`

Fluent builder:
- `internal` constructor: `(UHttpClient client, HttpMethod method, string url)`
- Methods: `WithHeader`, `WithHeaders`, `WithBody(byte[])`, `WithBody(string)`, `WithJsonBody(string)`, `WithJsonBody<T>`, `WithJsonBody<T>(T, JsonSerializerOptions)`, `WithTimeout`, `WithMetadata`, `WithBearerToken`, `Accept`, `ContentType`
- `Build()` — resolves relative URLs against `BaseUrl`, merges default + request headers, uses `_client._options.DefaultTimeout` as the timeout unless `WithTimeout()` was explicitly called (overrides the hardcoded 30s default in `UHttpRequest` constructor), returns `UHttpRequest`
- `SendAsync(ct)` — calls `Build()` then `_client.SendAsync()`

**Critical fixes from review:**
- **Multi-value headers in `WithHeaders`:** Iterate `headers.Names` + `GetValues()` to copy ALL values, not just first via enumerator
- **`WithJsonBody<T>` IL2CPP safety:** Add overload accepting `JsonSerializerOptions` for source-generated contexts. Document that default overload uses reflection (may fail under IL2CPP for complex types).
- **`WithJsonBody(string json)` overload (IL2CPP-recommended):** Accepts pre-serialized JSON string. This is the **recommended** approach for IL2CPP builds — users can serialize with their own IL2CPP-safe serializer (Unity's JsonUtility, Newtonsoft with AOT, or source-generated System.Text.Json). Sets `Content-Type: application/json` and converts to UTF-8 bytes.
  ```csharp
  public UHttpRequestBuilder WithJsonBody(string json)
  {
      return WithBody(Encoding.UTF8.GetBytes(json)).ContentType("application/json");
  }
  ```

---

## Step 4: `UHttpClient`

**File:** `Runtime/Core/UHttpClient.cs`

Main client:
- Constructor: optional `UHttpClientOptions` (null allowed, defaults to new `UHttpClientOptions()`)
- **Snapshot options at construction:** `_options = options?.Clone() ?? new UHttpClientOptions()` — prevents mutation after client creation. **Note:** Transport is a shared reference (not cloned). Users must not dispose a Transport instance passed via options while the client is alive.
- Resolves transport: `_options.Transport ?? HttpTransportFactory.Default` (factory uses `Lazy<T>` for thread-safe singleton)
- Tracks `_ownsTransport` bool: `true` **only** when user explicitly provides a transport via `UHttpClientOptions.Transport` **and** sets `DisposeTransport = true`. Factory-provided transport is a shared singleton — **never disposed by any individual client**.
  ```csharp
  _transport = _options.Transport ?? HttpTransportFactory.Default;
  _ownsTransport = (_options.Transport != null && _options.DisposeTransport);
  ```
- Verb methods: `Get`, `Post`, `Put`, `Delete`, `Patch`, `Head`, `Options` → return `UHttpRequestBuilder`
- `SendAsync(UHttpRequest, CancellationToken)`:
  1. Create `RequestContext`, record "RequestStart"
  2. Call `_transport.SendAsync(request, context, ct)`
  3. Record "RequestComplete", stop context
  4. On exception: record "RequestFailed", then:
     - `catch (UHttpException) { throw; }` — transport already mapped the error, do NOT re-wrap
     - `catch (Exception ex) { throw new UHttpException(new UHttpError(UHttpErrorType.Unknown, ex.Message, ex)); }` — safety net for unexpected non-transport exceptions only
  5. **Does NOT use `ConfigureAwait(false)`** — continuations return to the caller's `SynchronizationContext` (typically Unity main thread). The transport layer internally uses `ConfigureAwait(false)`.
- **Implements `IDisposable`** — dispose transport only if `_ownsTransport`. **Factory-provided transports are shared singletons and are NEVER disposed by clients.** Only user-provided transports with `DisposeTransport = true` are disposed.
- Error model: transport maps platform exceptions to `UHttpException`; client passes them through. HTTP 4xx/5xx are normal responses with body intact.

---

## Step 5: Replace `HttpTransportFactory`

**File:** `Runtime/Core/HttpTransportFactory.cs` (**full replacement** of existing Phase 2 implementation)

The Phase 2 implementation had a simple `_defaultTransport` field with a public setter. Phase 3 replaces it entirely with a `Lazy<T>`-based factory pattern for thread-safe lazy initialization. The public `set` accessor is removed to prevent conflicts between `Register()` and direct assignment.

**Design decisions:**
- `Register(Func<IHttpTransport>)` sets the factory and creates a new `Lazy<T>` instance
- `Default` getter returns `_lazy.Value` (thread-safe, guaranteed single construction)
- `Default` setter is removed — use `Register()` for factory registration or `SetForTesting()` for direct assignment in tests
- `Reset()` clears both `_factory` and `_lazy` (for testing only)
- `SetForTesting(IHttpTransport)` provides direct assignment for test mocks without going through factory

```csharp
private static volatile Func<IHttpTransport> _factory;
private static volatile Lazy<IHttpTransport> _lazy; // volatile for lock-free reads
private static readonly object _lock = new object();

public static void Register(Func<IHttpTransport> factory)
{
    if (factory == null) throw new ArgumentNullException(nameof(factory));
    lock (_lock)
    {
        _factory = factory;
        _lazy = new Lazy<IHttpTransport>(_factory, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}

// LOCK-FREE read path: Lazy<T>.Value is already thread-safe.
// The lock is only needed during Register() to prevent concurrent registration.
// This avoids lock contention on every request under high concurrency.
public static IHttpTransport Default
{
    get
    {
        var lazy = _lazy; // Single volatile read
        if (lazy == null)
            throw new InvalidOperationException(
                "No default transport configured. " +
                "Ensure TurboHTTP.Transport is included in your project. " +
                "If using IL2CPP and the module initializer did not fire, " +
                "call RawSocketTransport.EnsureRegistered() at startup.");
        return lazy.Value; // Lazy<T> handles thread-safe initialization internally
    }
}

/// <summary>
/// Set a transport directly for testing (bypasses factory).
/// </summary>
public static void SetForTesting(IHttpTransport transport)
{
    lock (_lock)
    {
        _lazy = new Lazy<IHttpTransport>(() => transport, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}

/// <summary>
/// Reset to initial state. For testing only.
/// Clears both the factory and any created transport.
/// </summary>
public static void Reset()
{
    lock (_lock)
    {
        _factory = null;
        _lazy = null;
    }
}
```

**Migration from Phase 2:**
- Remove: `public static IHttpTransport Default { set; }` — replaced by `SetForTesting()`
- Remove: `public static IHttpTransport Create()` — redundant, just use `Default`
- Phase 2 tests using `HttpTransportFactory.Default = mockTransport` → change to `HttpTransportFactory.SetForTesting(mockTransport)`

---

## Verification

1. All 4 new files + 1 modified compile in `TurboHTTP.Core` assembly
2. No references to `TurboHTTP.Transport` — dependency direction maintained
3. Existing Phase 2 tests still pass
4. `UHttpClient` can be instantiated with mock transport (set via `UHttpClientOptions.Transport`)
5. Builder resolves relative URLs, merges headers, handles JSON body
