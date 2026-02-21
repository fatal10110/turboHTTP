# UniTask Module

TurboHTTP treats `ValueTask` as a first-class citizen for high-performance, zero-allocation async methods. However, in the Unity ecosystem, [UniTask](https://github.com/Cysharp/UniTask) is the dominant zero-allocation awaitable. 

TurboHTTP provides native adapters to seamlessly integrate with `UniTask`.

## Enabling UniTask Support

If you have UniTask installed in your project, the macros `UNITASK_SUPPORT` and `UNITASK_WEBSOCKET_SUPPORT` will automatically light up.

You can include `TurboHTTP.UniTask` to access extension methods.

## Usage

You can safely await any `ValueTask` returning method from TurboHTTP inside a `UniTask` method.

```csharp
using Cysharp.Threading.Tasks;
using TurboHTTP.Core;

public class DataService : MonoBehaviour
{
    private UHttpClient _client;

    public async UniTask<MyData> FetchDataAsync()
    {
        // SendAsync() returns ValueTask<UHttpResponse>
        var response = await _client.Get("/api/data").SendAsync();
        
        return response.AsJson<MyData>();
    }
}
```

## WebSocket UniTask Extensions

The `WebSocket` module also has UniTask wrappers to avoid allocation in the receive loop.

```csharp
using Cysharp.Threading.Tasks;
using TurboHTTP.WebSocket;

public async UniTask ReceiveLoopAsync(UWebSocket ws, CancellationToken ct)
{
    var memory = new byte[1024].AsMemory();
    
    while (ws.State == WebSocketState.Open)
    {
        // ReceiveAsync normally returns ValueTask. 
        // UniTask integration ensures optimal execution context.
        var result = await ws.ReceiveAsync(memory, ct);
        
        if (result.MessageType == WebSocketMessageType.Text)
        {
            // Process
        }
    }
}
```

## Awaiters & SynchronizationContext

TurboHTTP requests avoid capturing the Unity `SynchronizationContext` automatically (internally using `.ConfigureAwait(false)`). When you await a TurboHTTP call in a `UniTask` method on the main thread, UniTask's custom awaiter seamlessly bridges you back to the main thread upon completion.
