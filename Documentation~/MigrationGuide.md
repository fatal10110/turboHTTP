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
*   **Async/Await:** TurboHTTP uses `async/await` natively. No Coroutines.
*   **Headers:** Use fluent `.WithHeader()` instead of `SetRequestHeader`.
*   **JSON:** Built-in JSON serialization helpers.
*   **Keep-Alive:** TurboHTTP pools connections by default; UnityWebRequest behavior varies by version.

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
