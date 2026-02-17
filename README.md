# TurboHTTP

[![Unity 2021.3+](https://img.shields.io/badge/unity-2021.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE.md)
[![Platform: All](https://img.shields.io/badge/Platform-Win%20%7C%20Mac%20%7C%20Linux%20%7C%20iOS%20%7C%20Android-lightgrey)](Documentation~/PlatformNotes.md)

**TurboHTTP** is a production-grade, modular HTTP client for Unity, designed for reliability, performance, and advanced observability. It serves as a modern alternative to `UnityWebRequest`, offering HTTP/2 support, detailed timeline tracing, and a fluent API.

## Key Features

*   **Modular Architecture:** Core client with optional specific modules (Retry, Cache, etc.).
*   **Modern Transport:** Raw socket-based transport supporting HTTP/1.1 and **HTTP/2** (with multiplexing).
*   **Observability:** Detailed timeline traces for every request (DNS, Connect, SSL, TTFB).
*   **Reliability:** Intelligent retry logic with idempotency awareness.
*   **Performance:** Zero-allocation focused design with memory pooling.
*   **Unity Integration:** Native `Texture2D`, `AudioClip`, and AssetBundle support.
*   **Testing:** Record/Replay mode for deterministic integration testing.

## Installation

### via Unity Package Manager (UPM)

1.  Open **Window** -> **Package Manager**.
2.  Click the **+** button in the top-left corner.
3.  Select **Add package from disk...**.
4.  Navigate to the `TurboHTTP` folder and select `package.json`.

Alternatively, if you are using a git URL:
1.  Select **Add package from git URL...**.
2.  Enter the git URL for this repository.

## Quick Start

```csharp
using TurboHTTP.Core;
using UnityEngine;

public class Example : MonoBehaviour
{
    private UHttpClient _client;

    private void Awake()
    {
        // specific configuration
        var options = new UHttpClientOptions
        {
             BaseUrl = "https://api.example.com"
        };
        _client = new UHttpClient(options);
    }

    private async void Start()
    {
        // Simple GET request
        var response = await _client.Get("/data")
            .WithHeader("Authorization", "Bearer token")
            .WithTimeout(TimeSpan.FromSeconds(10))
            .SendAsync();

        if (response.IsSuccessStatusCode)
        {
            var data = response.AsJson<MyData>();
            Debug.Log($"Received: {data.id}");
        }
        else
        {
            Debug.LogError($"Error: {response.StatusCode}");
        }
    }
    
    private void OnDestroy()
    {
        _client.Dispose();
    }
}
```

## Documentation

Comprehensive documentation is available in the `Documentation~` folder (visible in IDEs or via file explorer, hidden in Unity Editor project view).

*   [**Quick Start Guide**](Documentation~/QuickStart.md): Get up and running in minutes.
*   [**API Reference**](Documentation~/APIReference.md): Detailed API usage.
*   [**Platform Notes**](Documentation~/PlatformNotes.md): Platform-specific configuration (iOS, Android, IL2CPP).
*   [**Troubleshooting**](Documentation~/Troubleshooting.md): Common issues and solutions.
*   [**Migration Guide**](Documentation~/MigrationGuide.md): Moving from UnityWebRequest or BestHTTP.

## Development

For internal development documentation and implementation details, please refer to the [Development](Development/docs/README.md) directory.

## Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

This project is licensed under the MIT License - see the [LICENSE.md](LICENSE.md) file for details.
