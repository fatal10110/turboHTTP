# Migration Guide

Migrating from other HTTP libraries to TurboHTTP.

## From UnityWebRequest

### Making a Request

**UnityWebRequest:**
<!-- snippet: skip the UnityWebRequest side of the comparison is not TurboHTTP API -->
```csharp
IEnumerator GetRequest()
{
    using (UnityWebRequest webRequest = UnityWebRequest.Get("https://api.example.com"))
    {
        yield return webRequest.SendWebRequest();
        // ... Check .result, .error, etc.
    }
}
```

**TurboHTTP:**
<!-- snippet: with UHttpClient client = null; -->
```csharp
// Re-use one 'client' instance in production!
using var response = await client.Get("https://api.example.com").SendBufferedAsync();
// ... Check response.IsSuccessStatusCode
```

### Key Differences
*   **Async/Await:** TurboHTTP uses `async/await` natively. Coroutine wrappers exist for code that still needs them (`SendCoroutine`, `GetJsonCoroutine`; see [Unity Integration](Modules/Unity.md)).
*   **Headers:** Use fluent `.WithHeader()` instead of `SetRequestHeader`.
*   **JSON:** Built-in JSON serialization helpers.
*   **Keep-Alive:** TurboHTTP pools connections by default; UnityWebRequest behavior varies by version.

### Behavior differences: what a default client does not do
A `new UHttpClient()` has no interceptors. `UnityWebRequest` follows redirects, negotiates compression and (on some platforms) keeps cookies on its own; TurboHTTP does none of that until you add the interceptors from `TurboHTTP.Middleware`:

| Behavior | Default client | To get it |
| :--- | :--- | :--- |
| Redirects | A 3xx response is returned as is (`FollowRedirects` is `true` but only `RedirectInterceptor` acts on it; construction logs a one-time warning without it) | `RedirectInterceptor`, or `FollowRedirects = false` to silence the warning |
| Compression | No `Accept-Encoding` is sent and a `Content-Encoding` body is not decoded | `DecompressionInterceptor` |
| Cookies | `Set-Cookie` is ignored, no `Cookie` header is sent | `CookieInterceptor` |
| Relative URLs | `BaseUrl` is a prefix: `/users` and `users` are appended to `https://host/api` | include the version segment in `BaseUrl` |

<!-- snippet: with UHttpClientOptions options = new UHttpClientOptions(); -->
```csharp
using TurboHTTP.Middleware;

options.Interceptors.Add(new RedirectInterceptor());
options.Interceptors.Add(new CookieInterceptor());
options.Interceptors.Add(new DecompressionInterceptor());
```

## From BestHTTP

### Making a Request

**BestHTTP:**
<!-- snippet: skip BestHTTP API, not TurboHTTP -->
```csharp
var request = new HTTPRequest(new Uri("https://api.example.com"), HTTPMethods.Get, (req, resp) =>
{
    // Callback
});
request.Send();
```

**TurboHTTP:**
<!-- snippet: with UHttpClient client = null; -->
```csharp
using var response = await client.Get("https://api.example.com").SendBufferedAsync();
```

### Key Differences
*   **Client Instance:** BestHTTP uses stand-alone `HTTPRequest` objects. TurboHTTP uses a centralized `UHttpClient` (similar to .NET `HttpClient`) to manage connection pooling and configurations.
*   **Modularity:** Features like Caching and Retries are added as **interceptors** in TurboHTTP, rather than being built-in flags on the request.

## Checklist for Migration

1.  [ ] **Instantiation**: Create a single `UHttpClient` instance for your game/service lifetime.
2.  [ ] **Disposal**: Ensure you call `client.Dispose()` when the application quits or the service is destroyed to free up sockets.
3.  [ ] **Error Handling**: Switch from checking `.isNetworkError` strings to checking `response.IsSuccessStatusCode` or catching `UHttpException` for network and protocol failures (and for HTTP errors if you call `EnsureSuccessStatusCode`).
4.  [ ] **Response disposal**: dispose every `UHttpResponse` (`using var`), like a `UnityWebRequest`.
