# Migration Guide

Migrating from other HTTP libraries to TurboHTTP.

## From UnityWebRequest

### Making a Request

**UnityWebRequest:**
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
```csharp
async void GetRequest()
{
    // Re-use 'client' instance in production!
    var response = await client.Get("https://api.example.com").SendAsync();
    // ... Check response.IsSuccessStatusCode
}
```

### Key Differences
*   **Async/Await:** TurboHTTP uses `async/await` natively. No Coroutines.
*   **Headers:** Use fluent `.WithHeader()` instead of `SetRequestHeader`.
*   **JSON:** Built-in JSON serialization helpers.
*   **Keep-Alive:** TurboHTTP pools connections by default; UnityWebRequest behavior varies by version.

## From BestHTTP

### Making a Request

**BestHTTP:**
```csharp
var request = new HTTPRequest(new Uri("https://api.example.com"), HTTPMethods.Get, (req, resp) =>
{
    // Callback
});
request.Send();
```

**TurboHTTP:**
```csharp
var response = await client.Get("https://api.example.com").SendAsync();
```

### Key Differences
*   **Client Instance:** BestHTTP uses stand-alone `HTTPRequest` objects. TurboHTTP uses a centralized `UHttpClient` (similar to .NET `HttpClient`) to manage connection pooling and configurations.
*   **Modularity:** Features like Caching and Retries are added as **Middleware** in TurboHTTP, rather than being built-in flags on the request.

## Checklist for Migration

1.  [ ] **Instantiation**: Create a single `UHttpClient` instance for your game/service lifetime.
2.  [ ] **Disposal**: Ensure you call `client.Dispose()` when the application quits or the service is destroyed to free up sockets.
3.  [ ] **Error Handling**: Switch from checking `.isNetworkError` strings to checking `response.IsSuccessStatusCode` or catching `UHttpException` (if using `EnsureSuccessStatusCode`).
