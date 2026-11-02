# UniTask Module

TurboHTTP's async methods return `ValueTask`/`Task`. If you use [UniTask](https://github.com/Cysharp/UniTask), the optional `TurboHTTP.UniTask` assembly adds adapters.

## Enabling UniTask support

The assembly compiles only when the `com.cysharp.unitask` package (2.0.0 or newer) is installed, which defines `TURBOHTTP_UNITASK`. It is not auto-referenced: reference `TurboHTTP.UniTask` from your own `.asmdef`. The adapters need UniTask, so these snippets are not compiled with the rest of the documentation.

## Usage

`GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `HeadAsync` and `OptionsAsync` on `UHttpClient` return `UniTask<UHttpResponse>` (buffered), and `AsUniTask()` converts any request. Continuations resume on the main thread by UniTask's rules.

<!-- snippet: skip needs the UniTask package, which the shadow build does not have -->
```csharp
using Cysharp.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.UniTask;
using UnityEngine;

public class DataService : MonoBehaviour
{
    private UHttpClient _client;

    public async UniTask<MyData> FetchDataAsync()
    {
        using var response = await _client.GetAsync("https://api.example.com/data");
        return response.AsJson<MyData>();
    }
}
```

`WithTimeout(TimeSpan)` and `AttachToCancellationToken(token)` apply to a `UniTask<UHttpResponse>`; `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming` selects the player-loop timing used for continuations.

## WebSocket

`ConnectAsUniTask` connects an `IWebSocketClient` as a `UniTask`.

<!-- snippet: skip needs the UniTask package, which the shadow build does not have -->
```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TurboHTTP.UniTask;
using TurboHTTP.WebSocket;

public static class WebSocketExample
{
    public static async UniTask ConnectAsync(IWebSocketClient ws, CancellationToken ct)
    {
        await ws.ConnectAsUniTask(new Uri("wss://echo.websocket.events"), ct);
    }
}
```

Plain `Task`/`ValueTask` awaiting also works inside UniTask methods: TurboHTTP never captures the Unity `SynchronizationContext` (it uses `ConfigureAwait(false)` internally), so awaiting from a `UniTask` method returns you to the main thread through UniTask's own awaiter.
