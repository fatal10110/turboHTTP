# UniTask Module

TurboHTTP's async methods return `ValueTask`/`Task`. If you use [UniTask](https://github.com/Cysharp/UniTask), two optional assemblies add adapters: `TurboHTTP.UniTask` for HTTP (references only `TurboHTTP.Core`) and `TurboHTTP.UniTask.WebSocket` for the WebSocket client.

## Enabling UniTask support

Both assemblies compile only when the `com.cysharp.unitask` package (2.0.0 or newer) is installed, which defines `TURBOHTTP_UNITASK`. They are not auto-referenced: reference `TurboHTTP.UniTask` from your own `.asmdef`, and add `TurboHTTP.UniTask.WebSocket` only if you use the WebSocket adapters (an HTTP-only project then does not compile `TurboHTTP.WebSocket`). Both put their types in the `TurboHTTP.UniTask` namespace. The adapters need UniTask, so these snippets are not compiled with the rest of the documentation.

## Usage

`GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `HeadAsync` and `OptionsAsync` on `UHttpClient` return `UniTask<UHttpResponse>` (buffered), and `AsUniTask()` converts any request. Continuations resume on the main thread by UniTask's rules.

<!-- snippet: skip needs UniTask: the shadow build compiles the adapters against it (#311) but keeps it out of the documentation build, which also runs offline (--no-unitask) -->
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
        return response.AsJson<MyData>(); // [Serializable] DTO, see APIReference.md#json-serializers
    }
}
```

`TurboHttpUniTaskOptions.DefaultPlayerLoopTiming` selects the player-loop timing used for continuations.

## Timeouts and cancellation

Cancel or time out the request itself, not the `UniTask` that waits for it. The `CancellationToken` you pass to `GetAsync`/`AsUniTask` reaches the transport, so cancelling it aborts the request and releases its connection. A deadline set with `UHttpRequest.WithTimeout` is enforced by the transport and fails with `UHttpErrorType.Timeout`:

<!-- snippet: skip needs UniTask: the shadow build compiles the adapters against it (#311) but keeps it out of the documentation build, which also runs offline (--no-unitask) -->
```csharp
// Deadline enforced by the transport.
using var response = await _client.Get(url).WithTimeout(TimeSpan.FromSeconds(5)).AsUniTask(ct);

// Or cancel from your own timer: the same token reaches the transport.
using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
cts.CancelAfter(TimeSpan.FromSeconds(5));
using var other = await _client.GetAsync(url, cts.Token);
```

UniTask's `Timeout()` and `AttachExternalCancellation()` operators only stop waiting: the request keeps its connection until the transport's own timeout, and a response that arrives later is never disposed. If you use `Timeout()`, pass it the `CancellationTokenSource` whose token the request was sent with (`taskCancellationTokenSource:`) so the request is cancelled too. The adapter's own `WithTimeout(TimeSpan)` and `AttachToCancellationToken(token)` on a `UniTask<UHttpResponse>` are obsolete for the same reason; they still dispose a late response, but they cannot cancel the request.

## WebSocket

The WebSocket adapters are in the `TurboHTTP.UniTask.WebSocket` assembly; add it to your `.asmdef` references. `ConnectAsUniTask` connects an `IWebSocketClient` as a `UniTask`.

<!-- snippet: skip needs UniTask: the shadow build compiles the adapters against it (#311) but keeps it out of the documentation build, which also runs offline (--no-unitask) -->
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
