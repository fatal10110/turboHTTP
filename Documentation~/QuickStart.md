# TurboHTTP Quick Start Guide

Get started with TurboHTTP in under 5 minutes.

## Installation

1. **Import Package:**
   - Open Unity Package Manager (Window → Package Manager)
   - Click "+" → "Add package from disk"
   - Select `package.json` from the TurboHTTP folder

2. **Verify Installation:**
   - Check that "TurboHTTP - Complete HTTP Client" appears in Package Manager
   - No compile errors in Console

## Your First Request

### Simple GET Request

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();
        var response = await client.Get("https://api.example.com/data").SendAsync();

        if (response.IsSuccessStatusCode)
        {
            Debug.Log(response.GetBodyAsString());
        }
    }
}
```

### POST JSON Request

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();

        var data = new { username = "player1", score = 1000 };

        var response = await client
            .Post("https://api.example.com/scores")
            .WithJsonBody(data)
            .SendAsync();

        Debug.Log($"Status: {response.StatusCode}");
    }
}
```

### GET JSON with Deserialization

```csharp
using TurboHTTP.Core;
using UnityEngine;

[System.Serializable]
public class User
{
    public int id;
    public string name;
    public string email;
}

public class Example : MonoBehaviour
{
    async void Start()
    {
        var client = new UHttpClient();

        var user = await client.GetJsonAsync<User>(
            "https://jsonplaceholder.typicode.com/users/1"
        );

        Debug.Log($"User: {user.name} ({user.email})");
    }
}
```

## Common Patterns

### With Headers

```csharp
var response = await client
    .Get("https://api.example.com/protected")
    .WithBearerToken("your-token-here")
    .WithHeader("X-Custom-Header", "value")
    .SendAsync();
```

### With Timeout

```csharp
var response = await client
    .Get("https://api.example.com/slow")
    .WithTimeout(TimeSpan.FromSeconds(10))
    .SendAsync();
```

### Error Handling

```csharp
try
{
    var response = await client.Get("https://api.example.com/data").SendAsync();
    response.EnsureSuccessStatusCode();

    var data = response.AsJson<MyData>();
}
catch (UHttpException ex)
{
    Debug.LogError($"HTTP Error: {ex.HttpError.Type} - {ex.Message}");
}
```

## Next Steps

- [API Reference](APIReference.md) - Complete API documentation
- [Troubleshooting](Troubleshooting.md) - Common issues and solutions
- [Examples](../Samples~/) - Example projects
- [Platform Notes](PlatformNotes.md) - Platform-specific information
