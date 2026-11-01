# TurboHTTP

[![Unity 2021.3+](https://img.shields.io/badge/unity-2021.3%2B-blue.svg)](https://unity3d.com/get-unity/download)
[![License: Custom](https://img.shields.io/badge/License-Custom-yellow.svg)](LICENSE.md)
[![Platform: All](https://img.shields.io/badge/Platform-Win%20%7C%20Mac%20%7C%20Linux%20%7C%20iOS%20%7C%20Android-lightgrey)](Documentation~/PlatformNotes.md)

**TurboHTTP** is a production-grade, modular HTTP client for Unity, designed for reliability, performance, and advanced observability. It serves as a modern alternative to `UnityWebRequest`, offering HTTP/2 support, detailed timeline tracing, and a fluent API.

## Key Features

*   **Modular Architecture:** Core client with optional specific modules (Retry, Cache, etc.) and a zero-overhead plugin system.
*   **Modern Transport:** Raw socket-based transport supporting HTTP/1.1 and **HTTP/2** (with multiplexing).
*   **Resilience & Routing:** Happy Eyeballs dual-stack, HTTP proxy support (CONNECT tunnels, environment variables), and adaptive timeouts based on network quality (`TurboHTTP.Unity`).
*   **Enterprise Security:** Advanced OAuth 2.0 & OIDC support with automated token refresh.
*   **Mobile Readiness:** IL2CPP-safe on iOS and Android; experimental, opt-in background networking.
*   **Observability:** Detailed timeline traces for every request (DNS, Connect, SSL, TTFB).
*   **Reliability:** Interceptors for retries with idempotency awareness, RFC 9111 caching, redirects, cookies and concurrency limits.
*   **Performance:** Zero-allocation focused design with memory pooling and native `ValueTask` / `UniTask` support.
*   **Unity Integration:** `Texture2D`, `Sprite` and `AudioClip` decoding off the main thread, downloads to Unity paths, coroutine helpers.
*   **WebSockets:** RFC 6455 compliant WebSocket client with automatic reconnection and framing.
*   **Testing:** Record/Replay mode and Mock Server for deterministic testing.

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
using System;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using UnityEngine;

[Serializable]
public class MyData
{
    public int id;
    public string name;
}

public class Example : MonoBehaviour
{
    private UHttpClient _client;

    private void Awake()
    {
        var options = new UHttpClientOptions
        {
            BaseUrl = "https://api.example.com"
        };
        _client = new UHttpClient(options);
    }

    private async void Start()
    {
        try
        {
            // GET request; dispose the response when you are done with it.
            using var response = await _client.Get("/data")
                .WithHeader("Accept", "application/json")
                .WithTimeout(TimeSpan.FromSeconds(10))
                .SendBufferedAsync();

            if (response.IsSuccessStatusCode)
            {
                MyData data = response.AsJson<MyData>();
                Debug.Log($"Received: {data.id}");
            }
            else
            {
                // 4xx/5xx are normal responses, not exceptions.
                Debug.LogError($"Error: {response.StatusCode}");
            }
        }
        catch (UHttpException ex)
        {
            // Network, TLS, timeout and protocol failures.
            Debug.LogError($"Request failed: {ex.HttpError.Type}");
        }
    }

    private void OnDestroy()
    {
        _client.Dispose();
    }
}
```

Retries, caching, redirects, cookies and authentication are interceptors you add to `UHttpClientOptions.Interceptors`; see [Interceptors](Documentation~/Modules/Interceptors.md).

## Platform Support

*   **Supported:** Editor, Windows, macOS, Linux, iOS and Android (Mono and IL2CPP). See [Platform Notes](Documentation~/PlatformNotes.md).
*   **WebGL: unsupported in v1.0.** Browsers provide no raw sockets, so a WebGL player build fails at build time with an explanatory error.

The default HTTP and WebSocket transports register themselves at startup; no initialization code is needed.

## Assembly References

Scripts without an assembly definition (`Assembly-CSharp`) see every user-facing TurboHTTP module (Core, Transport, JSON, Retry, Auth, Cache, `TurboHTTP.Middleware` (redirects, cookies, decompression), RateLimit, Observability, Files, Unity, WebSocket). Code inside your own `.asmdef` must list the modules it uses (for example `TurboHTTP.Core`, `TurboHTTP.JSON`) in its references. `TurboHTTP.Testing`, `TurboHTTP.UniTask` and `TurboHTTP.Transport.BouncyCastle` are always referenced explicitly. The package's Samples each ship an assembly definition with the references they need. The BouncyCastle TLS provider is opt-in: add `TURBOHTTP_BOUNCYCASTLE` to Player Settings > Scripting Define Symbols to compile it (it then registers itself at startup and `TlsBackend.BouncyCastle` resolves); without the define it is not compiled.

## Documentation

Comprehensive documentation is available in the `Documentation~` folder (visible in IDEs or via file explorer, hidden in Unity Editor project view).

*   [**Quick Start Guide**](Documentation~/QuickStart.md): Get up and running in minutes.
*   [**API Reference**](Documentation~/APIReference.md): Detailed API usage.
*   **Module Deep Dives**:
    *   [Core](Documentation~/Modules/Core.md), [Transport](Documentation~/Modules/Transport.md), [WebSocket](Documentation~/Modules/WebSocket.md)
    *   [Unity Integration](Documentation~/Modules/Unity.md), [UniTask Support](Documentation~/Modules/UniTask.md)
    *   [Auth](Documentation~/Modules/Auth.md), [Interceptors](Documentation~/Modules/Interceptors.md), [Testing](Documentation~/Modules/Testing.md)
*   [**Platform Notes**](Documentation~/PlatformNotes.md): Platform-specific configuration (iOS, Android, IL2CPP).
*   [**Troubleshooting**](Documentation~/Troubleshooting.md): Common issues and solutions.
*   [**Migration Guide**](Documentation~/MigrationGuide.md): Moving from UnityWebRequest or BestHTTP.

## Development

For internal development documentation and implementation details, please refer to the [Development](Development/docs/README.md) directory.

## Contributing

We welcome contributions! Please see [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## License

This project is free for personal use and small companies. For enterprise commercial use, a separate agreement is required. See the [LICENSE.md](LICENSE.md) file for details.
