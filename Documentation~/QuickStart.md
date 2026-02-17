# TurboHTTP Quick Start Guide

Get started with TurboHTTP in under 5 minutes.

## Prerequisites

*   **Unity Version**: 2021.3 LTS or higher.
*   **Scripting Backend**: .NET Standard 2.1 (Project Settings -> Player -> Other Settings -> Api Compatibility Level).

## Installation

1.  **Open Unity Package Manager** (**Window** -> **Package Manager**).
2.  Click the **+** button -> **Add package from disk...**.
3.  Navigate to the `TurboHTTP` folder and select `package.json`.
4.  Wait for compilation to finish.

## Your First Request

### 1. Simple GET Request

Create a new script `Example.cs` and attach it to a GameObject in your scene.

```csharp
using TurboHTTP.Core;
using UnityEngine;
using System;

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
            var response = await _client.Get("https://jsonplaceholder.typicode.com/posts/1")
                .WithTimeout(TimeSpan.FromSeconds(10))
                .SendAsync();

            if (response.IsSuccessStatusCode)
            {
                Debug.Log($"Success: {response.GetBodyAsString()}");
            }
            else
            {
                Debug.LogError($"Error: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Request failed: {ex.Message}");
        }
    }
    
    private void OnDestroy()
    {
        // Always dispose the client when done.
        _client?.Dispose();
    }
}
```

### 2. POST JSON Request

Sending JSON data is straightforward.

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class PostExample : MonoBehaviour
{
    private UHttpClient _client = new UHttpClient();

    private async void Start()
    {
        var data = new { username = "player1", score = 1000 };

        var response = await _client.Post("https://api.example.com/scores")
            .WithJsonBody(data)
            .SendAsync();
            
        Debug.Log($"Status: {response.StatusCode}");
    }
    
    public void OnDestroy() => _client.Dispose();
}
```

### 3. GET JSON DTO

Deserializing responses directly into objects.

```csharp
[System.Serializable]
public class User
{
    public int id;
    public string name;
    public string email;
}

// ... inside your method
var user = await _client.GetJsonAsync<User>("https://jsonplaceholder.typicode.com/users/1");
Debug.Log($"User: {user.name}");
```

## Next Steps

*   [**API Reference**](APIReference.md): Explore the full API.
*   [**Advanced Features**](../Samples~/05-AdvancedFeatures): See the sample projects in the package.
*   [**Troubleshooting**](Troubleshooting.md): If you run into issues.
