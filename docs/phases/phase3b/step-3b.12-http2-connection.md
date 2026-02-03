# Step 3B.12: HTTP/2 Connection (Critical Path)

**File:** `Runtime/Transport/Http2/Http2Connection.cs`
**Depends on:** Steps 3B.1–3B.10 (all previous HTTP/2 components)
**Spec:** RFC 7540 Sections 3.5, 4, 5, 6, 8

## Purpose

Manage a single HTTP/2 connection: connection initialization (preface + SETTINGS), request sending with HPACK-encoded headers + DATA frames, background frame reading and dispatch, flow control, stream multiplexing, and graceful shutdown. This is the central orchestrator and the largest component of Phase 3B.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class Http2Connection : IDisposable
    {
        // Connection identity
        public string Host { get; }
        public int Port { get; }
        public bool IsAlive { get; }

        // Constructor
        public Http2Connection(Stream stream, string host, int port);

        // Lifecycle
        public Task InitializeAsync(CancellationToken ct);
        public Task<UHttpResponse> SendRequestAsync(UHttpRequest request, RequestContext context, CancellationToken ct);
        public void Dispose();
    }
}
```

## Fields

```csharp
// I/O
private readonly Stream _stream;
private readonly Http2FrameCodec _codec;

// HPACK (separate encoder/decoder, each with own dynamic table)
private readonly HpackEncoder _hpackEncoder;
private readonly HpackDecoder _hpackDecoder;

// Stream management
private readonly ConcurrentDictionary<int, Http2Stream> _activeStreams;
private int _nextStreamId = 1; // Client uses odd stream IDs

// Settings
private readonly Http2Settings _localSettings;   // What we sent
private readonly Http2Settings _remoteSettings;   // What server sent

// Flow control
private int _connectionSendWindow = Http2Constants.DefaultInitialWindowSize;
private int _connectionRecvWindow = Http2Constants.DefaultInitialWindowSize;
private readonly SemaphoreSlim _windowWaiter = new SemaphoreSlim(0);

// Write serialization
private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

// Lifecycle
private Task _readLoopTask;
private readonly CancellationTokenSource _cts = new CancellationTokenSource();
private volatile bool _goawayReceived;
private int _lastGoawayStreamId;

// Initialization handshake
private TaskCompletionSource<bool> _settingsAckTcs =
    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
```

## `InitializeAsync(CancellationToken ct)` — Connection Setup

RFC 7540 Section 3.5: The client connection preface starts with the 24-byte magic string, followed by a SETTINGS frame.

```
1. Write connection preface (24 bytes):
   "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"

2. Send initial SETTINGS frame:
   - ENABLE_PUSH = 0 (we reject server push)
   - MAX_CONCURRENT_STREAMS = 100

3. Start background read loop:
   _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token))

4. Wait for server's SETTINGS (non-ACK) — handled by read loop,
   which calls HandleSettingsFrameAsync and sends ACK.

5. Wait for server's SETTINGS ACK (acknowledging our SETTINGS):
   await _settingsAckTcs.Task with timeout (5 seconds).
   If timeout, throw connection error.
```

## `SendRequestAsync` — Sending a Request

### Step 1: Pre-flight checks

```csharp
if (_goawayReceived)
    throw new UHttpException(UHttpErrorType.NetworkError, "Connection received GOAWAY");
if (_cts.IsCancellationRequested)
    throw new UHttpException(UHttpErrorType.NetworkError, "Connection is closed");
```

### Step 2: Allocate stream ID

```csharp
var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
// Stream IDs: 1, 3, 5, 7, ... (client = odd)
```

### Step 3: Create stream object

```csharp
var stream = new Http2Stream(streamId, request, context, _remoteSettings.InitialWindowSize);
_activeStreams[streamId] = stream;

// Register per-request cancellation
stream.CancellationRegistration = ct.Register(() =>
{
    _ = SendRstStreamAsync(streamId, Http2ErrorCode.Cancel);
    stream.Cancel();
    _activeStreams.TryRemove(streamId, out _);
});
```

### Step 4: Build pseudo-headers + regular headers

```csharp
var headerList = new List<(string, string)>();

// Pseudo-headers (MUST come first per RFC 7540 Section 8.1.2.1)
headerList.Add((":method", request.Method.ToUpperString()));
headerList.Add((":scheme", request.Uri.Scheme.ToLowerInvariant()));
headerList.Add((":authority", BuildAuthorityValue(request.Uri)));
headerList.Add((":path", request.Uri.PathAndQuery ?? "/"));

// Regular headers (lowercase names)
foreach (var name in request.Headers.Names)
{
    // Skip HTTP/2 forbidden headers
    if (IsHttp2ForbiddenHeader(name)) continue;

    foreach (var value in request.Headers.GetValues(name))
        headerList.Add((name.ToLowerInvariant(), value));
}

// Auto-add user-agent if not set
if (!request.Headers.Contains("user-agent"))
    headerList.Add(("user-agent", "TurboHTTP/1.0"));
```

**HTTP/2 forbidden headers** (RFC 7540 Section 8.1.2.2):
- `connection`
- `transfer-encoding`
- `keep-alive`
- `proxy-connection`
- `upgrade`
- `host` (replaced by `:authority`)

### Step 5: HPACK encode headers

```csharp
await _writeLock.WaitAsync(ct);
try
{
    byte[] headerBlock = _hpackEncoder.Encode(headerList);
    bool hasBody = request.Body != null && request.Body.Length > 0;

    // Step 6: Send HEADERS frame(s)
    await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct);

    // Step 7: Send DATA frames (if body present)
    if (hasBody)
        await SendDataAsync(streamId, request.Body, stream, ct);

    // Update stream state
    stream.State = hasBody ? Http2StreamState.HalfClosedLocal : Http2StreamState.HalfClosedLocal;
}
finally
{
    _writeLock.Release();
}

context.RecordEvent("TransportH2RequestSent");

// Step 8: Wait for response
return await stream.ResponseTcs.Task;
```

### Sending HEADERS Frames

If the header block fits in one frame (≤ `_remoteSettings.MaxFrameSize`):
```
Send single HEADERS frame with END_HEADERS flag (+ END_STREAM if no body)
```

If the header block is larger:
```
Send HEADERS frame (without END_HEADERS, first MaxFrameSize bytes)
Send CONTINUATION frame(s) for remaining chunks
Last CONTINUATION frame has END_HEADERS flag
```

```csharp
private async Task SendHeadersAsync(int streamId, byte[] headerBlock, bool endStream, CancellationToken ct)
{
    int maxPayload = _remoteSettings.MaxFrameSize;
    int offset = 0;

    if (headerBlock.Length <= maxPayload)
    {
        // Single HEADERS frame
        var flags = Http2FrameFlags.EndHeaders;
        if (endStream) flags |= Http2FrameFlags.EndStream;

        await _codec.WriteFrameAsync(new Http2Frame
        {
            Type = Http2FrameType.Headers,
            Flags = flags,
            StreamId = streamId,
            Payload = headerBlock,
            Length = headerBlock.Length
        }, ct);
    }
    else
    {
        // HEADERS + CONTINUATION frames
        var firstPayload = new byte[maxPayload];
        Array.Copy(headerBlock, 0, firstPayload, 0, maxPayload);
        offset = maxPayload;

        var headersFlags = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
        await _codec.WriteFrameAsync(new Http2Frame
        {
            Type = Http2FrameType.Headers,
            Flags = headersFlags,
            StreamId = streamId,
            Payload = firstPayload,
            Length = firstPayload.Length
        }, ct);

        while (offset < headerBlock.Length)
        {
            int remaining = headerBlock.Length - offset;
            int chunkSize = Math.Min(remaining, maxPayload);
            var chunk = new byte[chunkSize];
            Array.Copy(headerBlock, offset, chunk, 0, chunkSize);
            offset += chunkSize;

            bool isLast = offset >= headerBlock.Length;
            await _codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Continuation,
                Flags = isLast ? Http2FrameFlags.EndHeaders : Http2FrameFlags.None,
                StreamId = streamId,
                Payload = chunk,
                Length = chunkSize
            }, ct);
        }
    }
}
```

### Sending DATA Frames with Flow Control

```csharp
private async Task SendDataAsync(int streamId, byte[] body, Http2Stream stream, CancellationToken ct)
{
    int offset = 0;
    while (offset < body.Length)
    {
        // Wait for flow control window availability
        int available = Math.Min(
            Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0), // read
            stream.WindowSize);
        available = Math.Min(available, _remoteSettings.MaxFrameSize);
        available = Math.Min(available, body.Length - offset);

        if (available <= 0)
        {
            // Block until WINDOW_UPDATE received
            await WaitForWindowUpdateAsync(ct);
            continue;
        }

        bool isLast = (offset + available) >= body.Length;
        var payload = new byte[available];
        Array.Copy(body, offset, payload, 0, available);

        await _codec.WriteFrameAsync(new Http2Frame
        {
            Type = Http2FrameType.Data,
            Flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
            StreamId = streamId,
            Payload = payload,
            Length = available
        }, ct);

        offset += available;
        Interlocked.Add(ref _connectionSendWindow, -available);
        stream.WindowSize -= available; // Under write lock, safe
    }
}
```

## `ReadLoopAsync` — Background Frame Dispatch

```csharp
private async Task ReadLoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await _codec.ReadFrameAsync(_remoteSettings.MaxFrameSize, ct);

            switch (frame.Type)
            {
                case Http2FrameType.Data:         HandleDataFrame(frame); break;
                case Http2FrameType.Headers:      HandleHeadersFrame(frame); break;
                case Http2FrameType.Continuation:  HandleContinuationFrame(frame); break;
                case Http2FrameType.Settings:     await HandleSettingsFrameAsync(frame, ct); break;
                case Http2FrameType.Ping:         await HandlePingFrameAsync(frame, ct); break;
                case Http2FrameType.GoAway:       HandleGoAwayFrame(frame); break;
                case Http2FrameType.WindowUpdate: HandleWindowUpdateFrame(frame); break;
                case Http2FrameType.RstStream:    HandleRstStreamFrame(frame); break;
                case Http2FrameType.PushPromise:  await RejectPushPromiseAsync(frame, ct); break;
                case Http2FrameType.Priority:     break; // Ignored (deprecated by RFC 9113)
            }
        }
    }
    catch (IOException) when (ct.IsCancellationRequested)
    {
        // Expected: connection closed during shutdown
    }
    catch (Exception ex)
    {
        // Unexpected error: fail all active streams
        FailAllStreams(ex);
    }
}
```

## Frame Handlers

### `HandleDataFrame(Http2Frame frame)`

```
1. Validate frame.StreamId != 0 (connection error PROTOCOL_ERROR if 0)
2. Look up stream in _activeStreams
3. If stream not found: send RST_STREAM(STREAM_CLOSED) — may be a late frame after stream closed
4. Append frame.Payload to stream.ResponseBody
5. Decrement _connectionRecvWindow by frame.Payload.Length
6. If _connectionRecvWindow < DefaultInitialWindowSize / 2:
   Send WINDOW_UPDATE on stream 0 to replenish (increment = DefaultInitialWindowSize - _connectionRecvWindow)
   Reset _connectionRecvWindow
7. Similarly for stream-level window
8. If END_STREAM flag:
   If stream.HeadersReceived: stream.Complete()
   Remove from _activeStreams, dispose stream
```

### `HandleHeadersFrame(Http2Frame frame)`

```
1. Validate frame.StreamId != 0
2. Look up stream in _activeStreams
3. Parse payload:
   - If HasPriority flag: skip 5 bytes (4 bytes stream dependency + 1 byte weight)
   - If Padded flag: read pad length from first byte, strip padding from end
4. Append remaining payload to stream.HeaderBlockBuffer
5. If END_HEADERS flag:
   - Decode header block: _hpackDecoder.Decode(stream.GetHeaderBlock())
   - Extract :status pseudo-header → stream.StatusCode
   - Build HttpHeaders from remaining headers
   - Set stream.ResponseHeaders
   - Set stream.HeadersReceived = true
   - Clear stream.HeaderBlockBuffer
6. If END_STREAM flag AND HeadersReceived:
   - stream.Complete()
   - Remove from _activeStreams
```

### `HandleContinuationFrame(Http2Frame frame)`

```
1. Look up stream in _activeStreams
2. Validate this follows a HEADERS or CONTINUATION for the same stream
3. Append frame.Payload to stream.HeaderBlockBuffer
4. If END_HEADERS flag:
   - Decode header block (same as HEADERS END_HEADERS handling)
5. If stream had END_STREAM on its HEADERS frame AND now HeadersReceived:
   - stream.Complete()
```

### `HandleSettingsFrameAsync(Http2Frame frame, CancellationToken ct)`

```
1. If ACK flag:
   - Signal _settingsAckTcs.TrySetResult(true)
   - Return
2. Validate frame.StreamId == 0 (connection error if not)
3. Parse settings: Http2Settings.ParsePayload(frame.Payload)
4. Save old InitialWindowSize
5. Apply each setting to _remoteSettings
6. If InitialWindowSize changed:
   - delta = newInitialWindowSize - oldInitialWindowSize
   - For each active stream: stream.WindowSize += delta
   - Check overflow (> 2^31-1 → FLOW_CONTROL_ERROR)
7. If HeaderTableSize changed:
   - _hpackEncoder.SetMaxDynamicTableSize(newSize)
8. Send SETTINGS ACK (empty payload, ACK flag, stream 0)
```

### `HandlePingFrameAsync(Http2Frame frame, CancellationToken ct)`

```
1. Validate frame.StreamId == 0
2. Validate frame.Payload.Length == 8 (FRAME_SIZE_ERROR if not)
3. If ACK flag: ignore (we don't send PINGs proactively in Phase 3)
4. If not ACK: echo back with ACK flag:
   await _codec.WriteFrameAsync(new Http2Frame
   {
       Type = Http2FrameType.Ping,
       Flags = Http2FrameFlags.Ack,
       StreamId = 0,
       Payload = frame.Payload  // Same 8 bytes
   }, ct);
```

### `HandleGoAwayFrame(Http2Frame frame)`

```
1. Validate frame.StreamId == 0
2. Parse payload:
   - Last-Stream-ID (4 bytes, 31-bit)
   - Error code (4 bytes)
   - Optional debug data (remaining bytes)
3. Set _goawayReceived = true
4. Set _lastGoawayStreamId = lastStreamId
5. Fail all streams with StreamId > lastStreamId:
   "Server sent GOAWAY, stream was not processed"
6. Streams with ID <= lastStreamId may complete normally
```

### `HandleWindowUpdateFrame(Http2Frame frame)`

```
1. Validate frame.Payload.Length == 4 (FRAME_SIZE_ERROR)
2. Parse window increment (31-bit, mask reserved bit)
3. If increment == 0: PROTOCOL_ERROR (RFC 7540 Section 6.9.1)
4. If frame.StreamId == 0:
   - Add to _connectionSendWindow (Interlocked)
   - Check overflow (> 2^31-1 → FLOW_CONTROL_ERROR)
   - Signal flow control waiters
5. If frame.StreamId > 0:
   - Look up stream
   - Add to stream.WindowSize
   - Check overflow
   - Signal flow control waiters
```

### `HandleRstStreamFrame(Http2Frame frame)`

```
1. Validate frame.StreamId != 0
2. Validate frame.Payload.Length == 4
3. Parse error code (4 bytes)
4. Look up stream in _activeStreams
5. Map error code to exception:
   - Cancel → stream.Cancel()
   - Other → stream.Fail(new UHttpException(NetworkError, $"RST_STREAM: {errorCode}"))
6. Remove from _activeStreams, dispose stream
```

### `RejectPushPromiseAsync(Http2Frame frame, CancellationToken ct)`

```
1. Parse promised stream ID from payload (4 bytes)
2. Send RST_STREAM with REFUSED_STREAM for the promised stream ID
```

## Helper Methods

### `BuildAuthorityValue(Uri uri)`

```csharp
private static string BuildAuthorityValue(Uri uri)
{
    var host = uri.Host;
    if (uri.HostNameType == UriHostNameType.IPv6)
        host = $"[{host}]";

    bool isDefaultPort = (uri.Scheme == "https" && uri.Port == 443)
                      || (uri.Scheme == "http" && uri.Port == 80);
    return isDefaultPort ? host : $"{host}:{uri.Port}";
}
```

### `IsHttp2ForbiddenHeader(string name)`

```csharp
private static bool IsHttp2ForbiddenHeader(string name)
{
    return string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "transfer-encoding", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "keep-alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "proxy-connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "upgrade", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "host", StringComparison.OrdinalIgnoreCase);
}
```

### `WaitForWindowUpdateAsync(CancellationToken ct)`

Block until a WINDOW_UPDATE frame is received. Use `SemaphoreSlim` or `TaskCompletionSource` signaled by `HandleWindowUpdateFrame`.

### `SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)`

Build and send RST_STREAM frame (4-byte payload = error code, big-endian).

### `FailAllStreams(Exception ex)`

Iterate `_activeStreams`, call `Fail` on each, clear the dictionary.

## `Dispose()` — Graceful Shutdown

```csharp
public void Dispose()
{
    _cts.Cancel();

    // Best-effort GOAWAY
    try
    {
        var goawayPayload = new byte[8];
        // Last-Stream-ID = _nextStreamId - 2 (last stream we opened)
        // Error code = NO_ERROR
        // ... encode big-endian ...
        var goaway = new Http2Frame
        {
            Type = Http2FrameType.GoAway,
            StreamId = 0,
            Payload = goawayPayload
        };
        _codec.WriteFrameAsync(goaway, CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
    }
    catch { /* best effort */ }

    // Wait for read loop to finish
    try { _readLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

    // Fail remaining streams
    FailAllStreams(new ObjectDisposedException(nameof(Http2Connection)));

    // Cleanup
    _writeLock?.Dispose();
    _cts?.Dispose();
    _windowWaiter?.Dispose();

    // Do NOT dispose _stream — owned by the connection manager/pool
}
```

## `IsAlive` Property

```csharp
public bool IsAlive =>
    !_goawayReceived &&
    !_cts.IsCancellationRequested &&
    _readLoopTask != null &&
    !_readLoopTask.IsCompleted;
```

## Thread Safety Summary

| Component | Thread Safety | Accessed By |
|-----------|--------------|-------------|
| `_codec.ReadFrameAsync` | Single-threaded | Read loop only |
| `_codec.WriteFrameAsync` | `_writeLock` serializes | Multiple streams + read loop (PING ACK, SETTINGS ACK) |
| `_activeStreams` | `ConcurrentDictionary` | Read loop + send callers |
| `_nextStreamId` | `Interlocked.Add` | Send callers |
| `_connectionSendWindow` | `Interlocked` | Read loop (WINDOW_UPDATE) + send (DATA) |
| `_connectionRecvWindow` | Read loop only | Read loop |
| `_hpackEncoder` | Under `_writeLock` | Send callers |
| `_hpackDecoder` | Read loop only | Read loop |
| `_goawayReceived` | `volatile` | Read loop (write) + send (read) |
| `Http2Stream.ResponseTcs` | `TrySet*` methods are thread-safe | Read loop + cancellation callback |

## Validation Criteria

- [ ] Connection preface sent correctly (24-byte magic + SETTINGS frame)
- [ ] SETTINGS exchange completes (client sends, server responds, both ACK)
- [ ] PING frames echoed with ACK flag and same 8-byte payload
- [ ] GOAWAY handling: new streams rejected, existing streams with ID ≤ last stream complete
- [ ] RST_STREAM fails individual stream without killing connection
- [ ] GET request: HEADERS with END_STREAM + END_HEADERS → receive HEADERS + DATA → UHttpResponse
- [ ] POST request: HEADERS + DATA with END_STREAM → receive response
- [ ] CONTINUATION frames handled (headers spanning multiple frames)
- [ ] Flow control: DATA sending blocks when window exhausted
- [ ] Flow control: WINDOW_UPDATE unblocks pending DATA sends
- [ ] PUSH_PROMISE rejected with RST_STREAM(REFUSED_STREAM)
- [ ] SETTINGS_INITIAL_WINDOW_SIZE change adjusts all active stream windows
- [ ] Connection-level and stream-level windows tracked independently
- [ ] `IsAlive` returns false after GOAWAY or disposal
- [ ] Dispose sends best-effort GOAWAY and fails all streams
