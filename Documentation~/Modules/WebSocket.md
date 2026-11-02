# WebSocket Module

TurboHTTP includes a robust, zero-allocation, fully RFC 6455 compliant WebSocket client.

## Creating a Connection

Create a client with `WebSocketClient.Create(options)` and connect it to a `ws://` or `wss://` URI. The raw-socket WebSocket transport (`TurboHTTP.WebSocket.Transport`) registers itself at startup, so no setup code is needed.

<!-- snippet: with CancellationToken ct = default; -->
```csharp
using TurboHTTP.WebSocket;

IWebSocketClient ws = WebSocketClient.Create(new WebSocketConnectionOptions());
await ws.ConnectAsync(new Uri("wss://echo.websocket.events"), ct);
// ... send and receive ...
await ws.CloseAsync(WebSocketCloseCode.NormalClosure, "bye", ct);
ws.Dispose();
```

The `TurboHTTP.Unity.WebSocket` module adds `client.WebSocket(url)` on `UHttpClient`, which connects a client that raises its events on the Unity main thread and inherits the TLS backend and cleartext setting of the HTTP client:

<!-- snippet: with UHttpClient httpClient = null; -->
```csharp
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;

IWebSocketClient ws = await httpClient.WebSocket("wss://echo.websocket.events");
ws.OnMessage += message => Debug.Log(message.Text);
```

### Cleartext (`ws://`)

`ws://` URIs and `http://` proxies are cleartext. They are refused before any socket is opened
(`WebSocketError.CleartextNotPermitted`) unless `WebSocketConnectionOptions.AllowCleartext` is
`true` and the platform policy permits the host. The option defaults to `false` on iOS and Android
players and `true` in the Editor and on desktop. Proxy Basic credentials are sent only when the
proxy's 407 offers `Basic`, and only when cleartext is allowed. See PlatformNotes, "Cleartext traffic".

## Sending and Receiving

### Sending

<!-- snippet: with IWebSocketClient ws = null; -->
```csharp
using TurboHTTP.WebSocket;

await ws.SendAsync("Hello World");                       // text frame

byte[] data = { 1, 2, 3 };
await ws.SendAsync(data);                                // binary frame
await ws.SendAsync(new ReadOnlyMemory<byte>(data, 0, 2));
```

### Receiving

Pull messages with `ReceiveAsync` (one message) or `ReceiveAllAsync` (an async stream). A `WebSocketMessage` holds a leased payload buffer and is `IDisposable`: dispose it once you have consumed it.

<!-- snippet: with IWebSocketClient ws = null; CancellationToken ct = default; -->
```csharp
using TurboHTTP.WebSocket;

while (ws.State == WebSocketState.Open)
{
    using WebSocketMessage message = await ws.ReceiveAsync(ct);
    if (message.IsText)
        Debug.Log($"Received: {message.Text}");
    else
        Debug.Log($"Received {message.Length} bytes");
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

Set a `WebSocketReconnectPolicy` on the connection options and `WebSocketClient.Create` returns a `ResilientWebSocketClient`, which reconnects with exponential backoff and jitter.

<!-- snippet: with CancellationToken ct = default; -->
```csharp
using TurboHTTP.WebSocket;

var options = new WebSocketConnectionOptions
{
    ReconnectPolicy = new WebSocketReconnectPolicy(maxRetries: 5, initialDelay: TimeSpan.FromSeconds(1))
};
var ws = (ResilientWebSocketClient)WebSocketClient.Create(options);

ws.OnReconnecting += (attempt, delay) => Debug.Log($"Reconnecting (attempt {attempt}) in {delay.TotalSeconds:F1}s");
ws.OnReconnected += () => Debug.Log("Reconnected!");
ws.OnClosed += (code, reason) => Debug.Log($"Closed: {code} {reason}");

await ws.ConnectAsync(new Uri("wss://api.example.com/stream"), options, ct);
```

`WebSocketReconnectPolicy.Default`, `.Infinite` and `.None` are ready-made policies.

## Proxies, TLS and limits

`WebSocketConnectionOptions.ProxySettings` (a `WebSocketProxySettings`) routes the connection through an HTTP CONNECT proxy; `TlsBackend` selects the TLS provider with the same rules as HTTP (see [Transport](Transport.md)); `MaxMessageSize`, `MaxFrameSize` and the receive-queue bounds cap what a peer can make the client buffer.

## Extensions & Compression

Per-Message Deflate (`permessage-deflate`, RFC 7692) is opt-in:

```csharp
using TurboHTTP.WebSocket;

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
