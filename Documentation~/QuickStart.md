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
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    private UHttpClient _client;

    private void Awake()
    {
        // Initialize the client once and reuse it.
        _client = new UHttpClient();
    }

    private async void Start()
    {
        try
        {
            // Dispose the response when you are done with it.
            using var response = await _client.Get("https://jsonplaceholder.typicode.com/posts/1")
                .WithHeader("Accept", "application/json")      // headers
                .WithTimeout(TimeSpan.FromSeconds(10))         // timeout for this request
                .SendBufferedAsync();

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
    }

    private void OnDestroy()
    {
        // Always dispose the client when done.
        _client?.Dispose();
    }
}
```

### 2. POST JSON

`WithJsonBody` (from `TurboHTTP.JSON`) serializes an object and sets `Content-Type: application/json`.

```csharp
using System;
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
    private readonly UHttpClient _client = new UHttpClient();

    private async void Start()
    {
        var data = new Score { username = "player1", score = 1000 };

        using var response = await _client.Post("https://api.example.com/scores")
            .WithJsonBody(data)
            .SendBufferedAsync();

        Debug.Log($"Status: {response.StatusCode}");
    }

    private void OnDestroy() => _client.Dispose();
}
```

### 3. GET a typed JSON object

Deserialize the response straight into a class:

```csharp
using System;
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
    private readonly UHttpClient _client = new UHttpClient();

    private async void Start()
    {
        User user = await _client.GetJsonAsync<User>("https://jsonplaceholder.typicode.com/users/1");
        Debug.Log($"User: {user.name}");

        // Or deserialize a response you already hold:
        using var response = await _client.Get("https://jsonplaceholder.typicode.com/users/2").SendBufferedAsync();
        if (response.TryAsJson(out User other))
            Debug.Log($"User: {other.name}");
    }

    private void OnDestroy() => _client.Dispose();
}
```

### 4. Base URL, default headers and timeout

<!-- snippet: with CancellationToken ct = default; -->
```csharp
var options = new UHttpClientOptions
{
    BaseUrl = "https://api.example.com",
    DefaultTimeout = TimeSpan.FromSeconds(15)
};
options.DefaultHeaders.Set("Accept", "application/json");
using var client = new UHttpClient(options);

using var response = await client.Get("/status").SendBufferedAsync(ct);
```

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
