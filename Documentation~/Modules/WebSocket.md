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

### Cleartext (`ws://`)

`ws://` URIs and `http://` proxies are cleartext. They are refused before any socket is opened
(`WebSocketError.CleartextNotPermitted`) unless `WebSocketConnectionOptions.AllowCleartext` is
`true` and the platform policy permits the host. The option defaults to `false` on iOS and Android
players and `true` in the Editor and on desktop. Proxy Basic credentials are sent only when the
proxy's 407 offers `Basic`, and only when cleartext is allowed. See PlatformNotes, "Cleartext traffic".

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

### Events vs. `ReceiveAsync`

`WebSocketClient` and `ResilientWebSocketClient` deliver messages through the `OnMessage` event, the
pull API (`ReceiveAsync` / `ReceiveAllAsync`), or both:

- **Event-only** (handlers on `OnMessage`, no pulling): every message is raised through the event and
  receiving never waits on the pull queue, so the client keeps reading the socket and answering
  ping/pong/close frames no matter how many messages arrive. `UnityWebSocketClient` and
  `UnityWebSocketBridge` work this way.
- **Pull-only** (no `OnMessage` handler): messages are queued losslessly, up to
  `WebSocketConnectionOptions.ReceiveQueueCapacity` messages (default 100) and
  `ReceiveQueueMaxBytes` payload bytes (default 8 MB). When either budget is reached, reading
  from the socket pauses until you call `ReceiveAsync` again (TCP backpressure). The keep-alive pong
  timeout is not charged while reading is paused. An empty queue always accepts one message, so a
  message up to `MaxMessageSize` is delivered even when it is larger than `ReceiveQueueMaxBytes`.
- **Mixed** (handlers subscribed and `ReceiveAsync` used): every message is raised through the event,
  and the pull queue keeps only the most recent messages that fit `ReceiveQueueCapacity` and
  `ReceiveQueueMaxBytes`; older unread messages are discarded. Each handler gets its own copy of the
  message.

### Message size limits

`MaxMessageSize` (default 4 MB) bounds every received message: single-frame, reassembled from
fragments, and after decompression. `MaxFrameSize` (default 4 MB) bounds one frame and must not be
larger than `MaxMessageSize` (`Validate` throws otherwise). A frame or message over its limit is
rejected from its header, before the payload is read, and the connection closes with code 1009
(Message Too Big).

### Cancelling a send

The `CancellationToken` passed to a send can cancel the send before its first byte is written (for
example while it waits for another send to finish); the connection stays open. Once a frame is on the
wire, a partial message cannot be recovered (RFC 6455 sections 5.2 and 5.4), so cancelling or failing
the send from that point aborts the connection (local close code 1006) and throws a
`WebSocketException` with `WebSocketError.SendFailed`.

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

Per-Message Deflate (`permessage-deflate`, RFC 7692) is opt-in:

```csharp
var options = new WebSocketConnectionOptions().WithCompression();          // optional
var strict = new WebSocketConnectionOptions().WithRequiredCompression();   // close 1010 if declined
```

The client offers `server_no_context_takeover` and requires it in the server's response, because
incoming messages are inflated without keeping context between messages. It never offers
`client_max_window_bits` (outgoing compression always uses a 15-bit window), so a response that sets
it is rejected. `PerMessageDeflateOptions` therefore only accepts `serverNoContextTakeover: true` and
`clientMaxWindowBits: 15`.

The handshake fails (`WebSocketError.ExtensionNegotiationFailed`, close code 1002) whenever the server
selects an extension the client did not offer, selects one twice, or uses parameters the client
cannot honour, whether compression is optional or required (RFC 6455 section 4.1). Raw extension
strings in `WebSocketConnectionOptions.Extensions` are not supported and fail `Validate`; register
extensions with `WithExtension` instead. Control frames with any RSV bit set close the connection
with 1002.
