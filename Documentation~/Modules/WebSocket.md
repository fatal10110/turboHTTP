# WebSocket Module

TurboHTTP includes a robust, zero-allocation, fully RFC 6455 compliant WebSocket client.

## Creating a Connection

WebSockets are created via the `UHttpClient.WebSockets` factory.

```csharp
var client = new UHttpClient();
var ws = client.WebSockets.Create("wss://echo.websocket.events");

// Connect
await ws.ConnectAsync(CancellationToken.None);
```

## Sending and Receiving (ValueTask)

The API is fully asynchronous and native to `ValueTask`, ensuring zero allocations on the hot path.

### Sending

```csharp
// Send text
await ws.SendTextAsync("Hello World");

// Send binary (using spans/memory)
byte[] data = new byte[] { 1, 2, 3 };
await ws.SendBinaryAsync(data.AsMemory());
```

### Receiving

Receiving is done in a loop using `ReceiveAsync`. The result contains the message type and the payload. For zero-allocation, payload is returned as a leased `Memory<byte>` that **must** be released or consumed.

```csharp
var buffer = new byte[4096]; // Reusable buffer
while (ws.State == WebSocketState.Open)
{
    var result = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
    
    if (result.MessageType == WebSocketMessageType.Text)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Debug.Log($"Received: {text}");
    }
    else if (result.MessageType == WebSocketMessageType.Close)
    {
        Debug.Log("Server initiated close.");
        break;
    }
}
```

## Reconnection & Resilience

The client supports automatic reconnections with exponential backoff.

```csharp
var options = new WebSocketOptions {
    EnableAutoReconnect = true,
    MaxReconnectAttempts = 5,
    InitialDelay = TimeSpan.FromSeconds(1)
};
var ws = client.WebSockets.Create("wss://api.example.com", options);

ws.OnReconnected += () => Debug.Log("Reconnected!");
ws.OnDisconnected += (code, reason) => Debug.Log($"Lost connection: {reason}");
```

## Extensions & Compression

Per-Message Deflate (`permessage-deflate`) extension is supported out of the box, reducing bandwidth usage.

```csharp
var options = new WebSocketOptions {
    EnableCompression = true // Default is true
};
```
