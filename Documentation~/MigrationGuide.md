# Migration Guide

Migrating from UnityWebRequest or BestHTTP to TurboHTTP.

## From UnityWebRequest

### Making a Request

**UnityWebRequest:**
```csharp
IEnumerator GetRequest()
{
    using (UnityWebRequest webRequest = UnityWebRequest.Get("https://api.example.com"))
    {
        yield return webRequest.SendWebRequest();
        // ... handle result
    }
}
```

**TurboHTTP:**
```csharp
async void GetRequest()
{
    var client = new UHttpClient();
    var response = await client.Get("https://api.example.com").SendAsync();
    // ... handle result
}
```

### Differences
- **Async/Await:** TurboHTTP is natively async/await, no coroutines required.
- **Headers:** Setting headers is fluent (`.WithHeader(...)`) vs `SetRequestHeader`.
- **JSON:** Built-in JSON support (`.WithJsonBody(...)`, `.AsJson<T>()`) vs manual `JsonUtility`.
- **Keep-Alive:** TurboHTTP supports connection pooling and keep-alive by default; UnityWebRequest does not reuse connections efficiently in all versions.

## From BestHTTP

### Making a Request

**BestHTTP:**
```csharp
var request = new HTTPRequest(new Uri("https://api.example.com"), HTTPMethods.Get, (req, resp) =>
{
    // ... handle result
});
request.Send();
```

**TurboHTTP:**
```csharp
var client = new UHttpClient();
var response = await client.Get("https://api.example.com").SendAsync();
// ... handle result
```

### Differences
- **API Style:** TurboHTTP uses a fluent builder API (`client.Get().WithHeader()...`) vs constructor params and callbacks.
- **Client Lifecycle:** TurboHTTP uses a reusable `UHttpClient` instance (like HttpClient in .NET) which manages the connection pool. BestHTTP requests are often standalone.
- **Modules:** TurboHTTP is modular; you add features (Retry, Cache) via middleware or modules, whereas BestHTTP often has them built-in or enabled via flags.

## Key Changes for Everyone

1. **Dispose your Client:** `UHttpClient` is `IDisposable`. Create one instance and reuse it for the lifetime of your app (or a specific scope), then dispose it. Do not create a new `UHttpClient` for every request.
2. **Main Thread:** `SendAsync` can be called from the main thread. Callbacks/continuations return to the context they were captured in (usually main thread in Unity if started there), but verify if doing heavy processing.
3. **Exceptions:** TurboHTTP does not throw exceptions for non-2xx status codes by default (check `IsSuccessStatusCode`), but does throw for network errors if you don't handle them or if you use `EnsureSuccessStatusCode`.
