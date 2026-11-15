# UniTask Module

TurboHTTP's async methods return `ValueTask`/`Task`. If you use [UniTask](https://github.com/Cysharp/UniTask), two optional assemblies add adapters: `TurboHTTP.UniTask` for HTTP (references only `TurboHTTP.Core`) and `TurboHTTP.UniTask.WebSocket` for the WebSocket client.

## Enabling UniTask support

Both assemblies compile only when `TURBOHTTP_UNITASK` is defined. Installing UniTask as the `com.cysharp.unitask` package (2.0.0 or newer) defines it automatically. A UniTask copied into `Assets/` or imported from a `.unitypackage` does not: add `TURBOHTTP_UNITASK` to **Project Settings > Player > Scripting Define Symbols** yourself (UniTask's assembly must be named `UniTask`, as it is in the official releases). Both assemblies are auto-referenced, so scripts without their own `.asmdef` (compiled into `Assembly-CSharp`) can call the adapters as soon as `TURBOHTTP_UNITASK` is defined; without the define they compile to nothing. Code in your own `.asmdef` references `TurboHTTP.UniTask` explicitly, and adds `TurboHTTP.UniTask.WebSocket` only if it uses the WebSocket adapters. Both put their types in the `TurboHTTP.UniTask` namespace. Because of that namespace, the bare name `UniTask` binds to `TurboHTTP.UniTask` (not to Cysharp's non-generic `UniTask` type) inside any namespace that starts with `TurboHTTP`, such as `TurboHTTP.Samples.*`. Keep your own code, including code copied from the samples, out of `TurboHTTP.*` namespaces, or write `Cysharp.Threading.Tasks.UniTask` in full there (`UniTask<T>` is not affected). The adapters need UniTask, so these snippets are not compiled with the rest of the documentation.

## Usage

`GetAsync`, `PostAsync`, `PutAsync`, `PatchAsync`, `DeleteAsync`, `HeadAsync` and `OptionsAsync` on `UHttpClient` return `UniTask<UHttpResponse>` (buffered), and `AsUniTask()` converts any request. Where your code resumes depends on the thread you call from; see [Which thread you resume on](#which-thread-you-resume-on).

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

### Which thread you resume on

TurboHTTP completes requests on thread-pool threads and uses `ConfigureAwait(false)` internally, so it never brings you back to the main thread by itself. The adapter awaits the response with a plain `await`, which captures the `SynchronizationContext` of the thread that made the call, and then applies the player-loop timing (the `playerLoopTiming` argument, else `TurboHttpUniTaskOptions.DefaultPlayerLoopTiming`):

- `PlayerLoopTiming.Update`, the default, adds no player-loop yield. Called from the main thread, you resume on the main thread, through Unity's `SynchronizationContext` that the adapter's `await` captured. Called from a thread without one, for example after `await UniTask.SwitchToThreadPool()` or inside `Task.Run`, you resume on the thread-pool thread that completed the request, where Unity APIs throw.
- Any other timing (for example `PlayerLoopTiming.PreUpdate` or `PostLateUpdate`) ends with `UniTask.Yield(timing)`, so you resume on the main thread in that player-loop phase from any calling thread.

To touch Unity objects after working off the main thread, call `await UniTask.SwitchToMainThread()` before using the response, or pass a timing other than `Update`.

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

Plain `Task`/`ValueTask` awaiting also works inside UniTask methods. TurboHTTP itself never captures the Unity `SynchronizationContext` (it uses `ConfigureAwait(false)` internally), but your own `await` on the returned `Task`/`ValueTask` does: called from the main thread, it resumes on the main thread through Unity's `SynchronizationContext`. That is the `Task`/`ValueTask` awaiter at work, not UniTask's; called from a thread-pool thread, the same `await` stays on the thread pool.
