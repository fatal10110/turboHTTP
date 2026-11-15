# TurboHTTP Quick Start Guide

Get started with TurboHTTP in under 5 minutes. Every snippet here is compiled against the current API by the repository's CI.

## Prerequisites

*   **Unity Version**: 2021.3 LTS or higher.
*   **Scripting Backend**: .NET Standard 2.1 (Project Settings -> Player -> Other Settings -> Api Compatibility Level).
*   **Platforms**: Editor, Standalone, iOS and Android. WebGL is unsupported in v1.0 (WebGL builds fail with an explanatory error).
*   **Assembly references**: scripts without an `.asmdef` see the TurboHTTP modules automatically; an `.asmdef` of your own must reference the modules it uses (for example `TurboHTTP.Core`, `TurboHTTP.JSON`).

## Installation

1.  **Open Unity Package Manager** (**Window** -> **Package Manager**).
2.  Click the **+** button -> **Add package from disk...**.
3.  Navigate to the `TurboHTTP` folder and select `package.json`.
4.  Wait for compilation to finish.

The package's **Samples** (Package Manager -> TurboHTTP -> Samples -> Import) are ready-made example scripts: each sample has its own assembly definition referencing the modules it needs.

## Your First Request

### 1. Simple GET Request

Create a new script `Example.cs` and attach it to a GameObject in your scene.

```csharp
using System;
using System.Threading;
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    private UHttpClient _client;
    private CancellationTokenSource _cts;

    private void Awake()
    {
        // Initialize the client once and reuse it.
        _client = new UHttpClient();
        _cts = new CancellationTokenSource();
    }

    private async void Start()
    {
        try
        {
            // Dispose the response when you are done with it.
            using var response = await _client.Get("https://jsonplaceholder.typicode.com/posts/1")
                .WithHeader("Accept", "application/json")      // headers
                .WithTimeout(TimeSpan.FromSeconds(10))         // timeout for this request
                .SendBufferedAsync(_cts.Token);

            if (response.IsSuccessStatusCode)
            {
                Debug.Log($"Success: {response.GetBodyAsString()}");
            }
            else
            {
                // 4xx and 5xx are normal responses, not exceptions.
                Debug.LogError($"HTTP {(int)response.StatusCode}: {response.GetBodyAsString()}");
            }
        }
        catch (UHttpException ex)
        {
            // Network, TLS, timeout and protocol failures.
            Debug.LogError($"Request failed: {ex.HttpError.Type} - {ex.HttpError.Message}");
        }
        catch (OperationCanceledException)
        {
            // The object was destroyed while the request was in flight.
        }
    }

    private void OnDestroy()
    {
        // Cancel in-flight requests first, then dispose the client; disposing under a running
        // request is not a supported way to stop it.
        _cts?.Cancel();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
```

### 2. POST JSON

`WithJsonBody` (from `TurboHTTP.JSON`) serializes an object and sets `Content-Type: application/json`. The JSON helpers use Unity's `JsonUtility` by default (registered at startup), so DTOs are `[Serializable]` classes with public fields; see [JSON serializers](APIReference.md#json-serializers) for other shapes and custom serializers.

```csharp
using System;
using System.Threading;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

[Serializable]
public class Score
{
    public string username;
    public int score;
}

public class PostExample : MonoBehaviour
{
    // Create the client in Awake, not in a field initializer: Unity can construct a
    // MonoBehaviour outside play mode and during deserialization, leaving stray clients.
    private UHttpClient _client;
    private CancellationTokenSource _cts;

    private void Awake()
    {
        _client = new UHttpClient();
        _cts = new CancellationTokenSource();
    }

    private async void Start()
    {
        try
        {
            var data = new Score { username = "player1", score = 1000 };

            using var response = await _client.Post("https://api.example.com/scores")
                .WithJsonBody(data)
                .SendBufferedAsync(_cts.Token);

            Debug.Log($"Status: {response.StatusCode}");
        }
        catch (UHttpException ex)
        {
            Debug.LogError($"Request failed: {ex.HttpError.Type} - {ex.HttpError.Message}");
        }
        catch (OperationCanceledException)
        {
            // Destroyed while the request was in flight.
        }
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
```

### 3. GET a typed JSON object

Deserialize the response straight into a class:

```csharp
using System;
using System.Threading;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

[Serializable]
public class User
{
    public int id;
    public string name;
    public string email;
}

public class UserExample : MonoBehaviour
{
    private UHttpClient _client;
    private CancellationTokenSource _cts;

    private void Awake()
    {
        _client = new UHttpClient();
        _cts = new CancellationTokenSource();
    }

    private async void Start()
    {
        try
        {
            User user = await _client.GetJsonAsync<User>("https://jsonplaceholder.typicode.com/users/1", _cts.Token);
            Debug.Log($"User: {user.name}");

            // Or deserialize a response you already hold:
            using var response = await _client.Get("https://jsonplaceholder.typicode.com/users/2").SendBufferedAsync(_cts.Token);
            if (response.TryAsJson(out User other))
                Debug.Log($"User: {other.name}");
        }
        catch (UHttpException ex)
        {
            Debug.LogError($"Request failed: {ex.HttpError.Type} - {ex.HttpError.Message}");
        }
        catch (OperationCanceledException)
        {
            // Destroyed while the request was in flight.
        }
    }

    private void OnDestroy()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _client?.Dispose();
    }
}
```

### 4. Base URL, default headers and timeout

`BaseUrl` is a prefix, not an RFC 3986 base: a request path is appended to it after its leading slashes are stripped, so `/status` and `status` both resolve to `https://api.example.com/v1/status` below, and the request cannot leave the `BaseUrl` host. Include the version segment in `BaseUrl`.

<!-- snippet: with CancellationToken ct = default; -->
```csharp
using TurboHTTP.Middleware;

var options = new UHttpClientOptions
{
    BaseUrl = "https://api.example.com/v1",
    DefaultTimeout = TimeSpan.FromSeconds(15)
};
options.DefaultHeaders.Set("Accept", "application/json");

// A plain client has no interceptors (see "What a default client does not do" below).
// Canonical order, outermost first: redirects, cookies, decompression.
options.Interceptors.Add(new RedirectInterceptor());
options.Interceptors.Add(new CookieInterceptor());
options.Interceptors.Add(new DecompressionInterceptor());
using var client = new UHttpClient(options);

using var response = await client.Get("/status").SendBufferedAsync(ct); // https://api.example.com/v1/status
```

#### What a default client does not do

A `new UHttpClient()` runs no interceptors, so unlike `UnityWebRequest` or `HttpClient`:

*   **No redirects.** A 3xx response is returned as is. `FollowRedirects` (default `true`) only takes effect together with `RedirectInterceptor`; without one the client logs a one-time warning at construction. Add the interceptor, or set `FollowRedirects = false` to return 3xx on purpose.
*   **No `Accept-Encoding` and no decoding.** No compression is requested, and a `Content-Encoding` body you receive anyway is handed over undecoded. `DecompressionInterceptor` adds `Accept-Encoding` and decodes gzip and deflate with a size bound.
*   **No cookies.** `Set-Cookie` is ignored and no `Cookie` header is sent. `CookieInterceptor` keeps a per-client jar.

All three live in the `TurboHTTP.Middleware` module (reference it from your `.asmdef`); see [Interceptors](Modules/Interceptors.md) for ordering.

### 5. Large downloads: streaming

A buffered response keeps the whole body in memory. For large bodies read the stream instead; see [Core](Modules/Core.md), "Buffered and streaming responses", or use `FileDownloader` (Samples, "File Download").

## Next Steps

*   [**API Reference**](APIReference.md): Explore the full API.
*   **Modules deeply explained:**
    *   [Core](Modules/Core.md), [Transport, TLS & proxies](Modules/Transport.md), [WebSockets](Modules/WebSocket.md)
    *   [Unity Integration](Modules/Unity.md), [UniTask Support](Modules/UniTask.md)
    *   [Auth](Modules/Auth.md), [Interceptors](Modules/Interceptors.md), [Testing](Modules/Testing.md)
*   [**Samples**](../Samples~): Basic usage, JSON API, file download, authentication and advanced features (retry, cache, concurrency).
*   [**Platform Notes**](PlatformNotes.md): iOS, Android, IL2CPP.
*   [**Troubleshooting**](Troubleshooting.md): If you run into issues.
