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
- `Clone()` — deep-copies headers/middleware list; Transport is shared reference (documented in XML comment)

---

## Step 3: `UHttpRequestBuilder`

**File:** `Runtime/Core/UHttpRequestBuilder.cs`

Fluent builder:
- `internal` constructor: `(UHttpClient client, HttpMethod method, string url)`
- Methods: `WithHeader`, `WithHeaders`, `WithBody(byte[])`, `WithBody(string)`, `WithJsonBody(string)`, `WithJsonBody<T>`, `WithJsonBody<T>(T, JsonSerializerOptions)`, `WithTimeout`, `WithMetadata`, `WithBearerToken`, `Accept`, `ContentType`
- `Build()` — resolves relative URLs against `BaseUrl`, merges default + request headers, returns `UHttpRequest`
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
- Constructor: optional `UHttpClientOptions` (defaults to new)
- **Snapshot options at construction:** `_options = options?.Clone() ?? new UHttpClientOptions()` — prevents mutation after client creation
- Resolves transport: `_options.Transport ?? HttpTransportFactory.Default`
- Tracks `_ownsTransport` bool: `true` when from factory, `false` when user-provided
- Verb methods: `Get`, `Post`, `Put`, `Delete`, `Patch`, `Head`, `Options` → return `UHttpRequestBuilder`
- `SendAsync(UHttpRequest, CancellationToken)`:
  1. Create `RequestContext`, record "RequestStart"
  2. Call `_transport.SendAsync(request, context, ct)`
  3. Record "RequestComplete", stop context
  4. On exception: record "RequestFailed", wrap in `UHttpException`
- **Implements `IDisposable`** — dispose transport only if `_ownsTransport`
- Error model: transport-level errors are exceptions; HTTP 4xx/5xx are normal responses with body

---

## Step 5: Update `HttpTransportFactory`

**File:** `Runtime/Core/HttpTransportFactory.cs` (modify existing)

Add lazy factory registration:

```csharp
private static volatile Func<IHttpTransport> _factory;
private static volatile IHttpTransport _defaultTransport;

public static void Register(Func<IHttpTransport> factory)
{
    _factory = factory ?? throw new ArgumentNullException(nameof(factory));
}

public static IHttpTransport Default
{
    get
    {
        if (_defaultTransport == null && _factory != null)
        {
            // Thread-safe lazy initialization — prevent duplicate transport creation
            var transport = _factory();
            var existing = Interlocked.CompareExchange(ref _defaultTransport, transport, null);
            if (existing != null)
            {
                // Another thread won the race — dispose our duplicate
                (transport as IDisposable)?.Dispose();
            }
        }
        if (_defaultTransport == null)
            throw new InvalidOperationException("No default transport configured...");
        return _defaultTransport;
    }
    set => _defaultTransport = value;
}
```

Keep existing `Reset()` method (clears both `_factory` and `_defaultTransport`).

---

## Verification

1. All 4 new files + 1 modified compile in `TurboHTTP.Core` assembly
2. No references to `TurboHTTP.Transport` — dependency direction maintained
3. Existing Phase 2 tests still pass
4. `UHttpClient` can be instantiated with mock transport (set via `UHttpClientOptions.Transport`)
5. Builder resolves relative URLs, merges headers, handles JSON body
