# WebSocket Module

TurboHTTP includes an RFC 6455 WebSocket client on its raw socket transport. Frame reads and writes
use pooled buffers, but the receive path still allocates per message: every received message is a
`WebSocketMessage` object, text messages carry a decoded `string`, and each `OnMessage` handler gets a
detached copy. The protocol cases the client is checked against, and its documented deviations, are
listed under [Interop](#interop).

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

The `TurboHTTP.Unity.WebSocket` module adds `client.WebSocket(url)` on `UHttpClient`, which connects a client that raises its events on the Unity main thread and inherits these settings of the HTTP client:

<!-- snippet: with UHttpClient httpClient = null; -->
```csharp
using TurboHTTP.Unity.WebSocket;
using TurboHTTP.WebSocket;

IWebSocketClient ws = await httpClient.WebSocket("wss://echo.websocket.events");
ws.OnMessage += message => Debug.Log(message.Text);
```

What the connection inherits from `UHttpClientOptions`, and nothing else:

- `TlsBackend` and `AllowCleartext`.
- From `DefaultHeaders`, only `User-Agent`, `Accept-Language` and `Origin`. Every other default
  header stays on the HTTP client: credentials (`Authorization`, `Cookie`, API-key headers) are never
  forwarded, to the `BaseUrl` origin or any other host, and `Proxy-Authorization`, hop-by-hop and
  handshake headers (`Host`, `Connection`, `Upgrade`, `Sec-WebSocket-*`) are never copied.
- `Proxy`: the proxy is chosen for the WebSocket URL as for an HTTP request to the same host and port
  (`wss` as `https`), so `BypassList` (all its rule forms) and, unless
  `UseEnvironmentVariables = false`, `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY` apply. The proxy
  credentials are sent only when `AllowPlaintextProxyAuth` is `true` (the leg to an `http://` proxy
  is cleartext). A proxy variable that names a non-`http://` proxy fails with `UHttpException`.

To send other headers (for example a token), create the client with
`WebSocketClient.Create(options)`, set `WebSocketConnectionOptions.CustomHeaders`, and wrap it in
`new UnityWebSocketBridge(client)` for main-thread events.

**Subscription order.** The task completes once the handshake has, so `OnConnected` has already
been raised and a handler you attach afterwards does not see it: the returned client is open. Messages
are not lost before you subscribe: the client holds the messages received before the first
`OnMessage` subscription (at most 100 messages or 8 MB, oldest dropped first with
`OnMessageDropped`) and delivers them, in order, to that subscriber. The hold also ends, and its
messages are disposed, when you start pulling instead (`ReceiveAsync`, `ReceiveAllAsync`,
`ReceiveAsCoroutine`, whose queue already has them) and when you dispose the client. If the connect
fails, the created client is disposed before the exception reaches you.

### Main-thread delivery under load

`UnityWebSocketBridge` (and so `UnityWebSocketClient`) posts each event to `MainThreadDispatcher`.
When its user queue is full (`MainThreadDispatcherSettings.UserQueueCapacity`, policy
`BackpressurePolicy`), message, metrics, quality and reconnect callbacks are dropped: a dropped
message is disposed, `OnMessageDropped` is raised on the thread that saw the drop, and at most one
warning (with the drop count since the previous one) is logged every 10 seconds. `OnConnected` and `OnClosed` are never dropped: they queue behind the
messages already waiting and move to the dispatcher's control lane when the user queue rejects or
evicts them, so under saturation they can arrive before older queued messages. `OnError` takes the
same path, with at most 32 errors waiting on the control lane at once. Work the
user queue does not run fails with `MainThreadDispatchRejectedException` (its `Reason` says why),
including work evicted by `DropOldest`.

`UnityWebSocketClient` raises `OnDisconnectedEvent` exactly once per connection, also when the close
fails or times out (code 1006). `Connect` while a `Disconnect` is still closing the previous
connection (for example pause, then resume) waits for that close; `Disconnect` during a connect
cancels it. No UnityEvent is raised after the component is destroyed.

`ReceiveAsCoroutine` returns a `UnityWebSocketReceiveCoroutine`. Unity does not dispose a coroutine
it stops, so call `Dispose()` on the handle when you `StopCoroutine` it; destroying the callback owner
or canceling the token ends it without that call.

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

Messages that arrived before the server's close frame stay readable after `State` becomes `Closed`:
`ReceiveAsync` returns them in order and only then throws `WebSocketException` with
`WebSocketError.ConnectionClosed` and the server's close code. A loop that must not miss the last
messages reads until that exception instead of testing `State`.

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

Keep-alive (`PingInterval`, default 25 s; `PongTimeout`, default 10 s) treats any bytes arriving from the
server as a sign of life, so a large message on a slow link does not end the connection while it is still
arriving. A ping that cannot be sent within `PongTimeout` because the server stopped reading ends the
connection with `WebSocketError.PongTimeout`, and the send that was stuck fails. A pong timeout is checked
again after a short grace period (at most one second) before the connection is closed, and
in Unity players app resume is reported to every connection (`WebSocketClient`,
`ResilientWebSocketClient` and `UnityWebSocketClient` alike), so time spent suspended does not count against
the timeout.

### Message size limits

`MaxMessageSize` (default 4 MB) bounds every received message: single-frame, reassembled from
fragments, and after decompression. `MaxFrameSize` (default 4 MB) bounds one frame and must not be
larger than `MaxMessageSize` (`Validate` throws otherwise). A frame or message over its limit is
rejected from its header, before the payload is read, and the connection closes with code 1009
(Message Too Big). `MaxFragmentCount` (default 4096) bounds how many frames one message may arrive in:
RFC 6455 sets no fragment limit and `MaxMessageSize` already bounds the bytes, so this only stops a
message that never ends in empty or tiny fragments. The default admits a 4 MB message in 1 KB frames;
raise it together with `MaxMessageSize` for peers that send large messages in small frames
(`MaxMessageSize <= MaxFrameSize * MaxFragmentCount` must hold). The fragment past the limit closes the
connection with 1009.

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

A failed connect reports its cause in `WebSocketException.Error` and in `HttpError.Type`, as HTTP requests
do: `Timeout` (the handshake or connect timeout expired; `UHttpErrorType.Timeout`), `CertificateError`
(the server certificate was rejected; `UHttpErrorType.CertificateError`), `DnsResolutionFailed`,
`InvalidRequest` (for example a URI that is not `ws://`/`wss://`), and `HandshakeFailed` with the server's
status in `HandshakeStatusCode` and `HttpError.StatusCode`. `IsRetryable()` follows the HTTP error model.
The resilient client stops reconnecting on failures that repeat on every attempt: a rejected certificate,
an invalid request, a cleartext refusal, a 407 from the proxy and a 4xx handshake rejection other than 408
and 429. Protocol violations from the server keep their specific error (`InvalidFrame`, `ReservedOpcode`,
`MaskedServerFrame`, `InvalidUtf8`, ...).

## Proxies, TLS and limits

`WebSocketConnectionOptions.ProxySettings` (a `WebSocketProxySettings`) routes the connection through an HTTP CONNECT proxy; `TlsBackend` selects the TLS provider with the same rules as HTTP (see [Transport](Transport.md)); left at `Auto`, the backend passed to the `RawSocketWebSocketTransport` constructor applies; `MaxMessageSize`, `MaxFrameSize` and the receive-queue bounds cap what a peer can make the client buffer.

## Extensions & Compression

Per-Message Deflate (`permessage-deflate`, RFC 7692) is opt-in and **experimental**: its verification
gates are still open (issue #104), so treat it as unvalidated for production traffic until they close.

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

When the server's response does not include `client_no_context_takeover`, the server may keep one
inflater for all client messages (RFC 7692 section 7.2.2). Every compressed client message therefore
ends at a sync flush (RFC 7692 section 7.2.1), so such a server can keep its inflater. On runtimes
whose `DeflateStream` cannot write a sync flush (Unity Mono, and IL2CPP with the same class libraries
expected to behave the same), a built-in managed encoder with fixed Huffman codes compresses those
messages instead; its ratio is somewhat lower than `DeflateStream`'s. Incoming messages are always
decompressed.

The handshake fails (`WebSocketError.ExtensionNegotiationFailed`, close code 1002) whenever the server
selects an extension the client did not offer, selects one twice, or uses parameters the client
cannot honour, whether compression is optional or required (RFC 6455 section 4.1). Raw extension
strings in `WebSocketConnectionOptions.Extensions` are not supported and fail `Validate`; register
extensions with `WithExtension` instead. Control frames with any RSV bit set close the connection
with 1002.

### Interop

The client is checked against the [Autobahn testsuite](https://github.com/crossbario/autobahn-testsuite)
fuzzing server (framing, ping/pong, reserved bits, opcodes, fragmentation, UTF-8, close handling,
limits and `permessage-deflate`). Cases that need a parameter the client declines by design (above)
fail the handshake on purpose. The gate, its results and every documented exception are in
`Tools~/autobahn/README.md` in the package repository.
