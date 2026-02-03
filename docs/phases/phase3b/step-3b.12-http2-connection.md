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

**REVIEW FIX [R2-6]:** Guard against stream ID overflow (max 2^31-1 per RFC 7540 Section 5.1.1).

```csharp
var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
// Stream IDs: 1, 3, 5, 7, ... (client = odd)
if (streamId < 0 || streamId > int.MaxValue)
    throw new UHttpException(UHttpErrorType.NetworkError, "Stream ID space exhausted, close and reopen connection");
```

### Step 3: Create stream object

**REVIEW FIX [A6]:** Check if already cancelled before registering callback. Dispose stream on cancellation.

```csharp
var stream = new Http2Stream(streamId, request, context, _remoteSettings.InitialWindowSize);
_activeStreams[streamId] = stream;

// REVIEW FIX [R2-4]: Re-check shutdown after adding to _activeStreams
// (FailAllStreams sets _goawayReceived before clearing, preventing race)
if (_goawayReceived || _cts.IsCancellationRequested)
{
    _activeStreams.TryRemove(streamId, out _);
    stream.Fail(new UHttpException(UHttpErrorType.NetworkError, "Connection closed during stream creation"));
    stream.Dispose();
    throw new UHttpException(UHttpErrorType.NetworkError, "Connection is closed");
}

// REVIEW FIX [A6]: Check if already cancelled before registering
if (ct.IsCancellationRequested)
{
    _activeStreams.TryRemove(streamId, out _);
    stream.Cancel();
    stream.Dispose();
    throw new OperationCanceledException(ct);
}

// Register per-request cancellation
stream.CancellationRegistration = ct.Register(() =>
{
    _ = SendRstStreamAsync(streamId, Http2ErrorCode.Cancel);
    stream.Cancel();
    _activeStreams.TryRemove(streamId, out _);
    stream.Dispose();  // REVIEW FIX [A6]: dispose after removal
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

### Step 5: HPACK encode headers + send HEADERS

**REVIEW FIX [R2-1] (Critical):** The write lock is held only for HEADERS frame(s), NOT across the entire body send. `SendDataAsync` acquires/releases `_writeLock` per DATA frame. This prevents deadlock: if the send window is exhausted and the sender blocks in `WaitForWindowUpdateAsync`, the read loop can still acquire `_writeLock` to send PING ACK, SETTINGS ACK, or WINDOW_UPDATE frames.

```csharp
// Acquire write lock for HPACK encoding + HEADERS (must be atomic to protect encoder state)
await _writeLock.WaitAsync(ct);
try
{
    byte[] headerBlock = _hpackEncoder.Encode(headerList);
    bool hasBody = request.Body != null && request.Body.Length > 0;

    // Step 6: Send HEADERS frame(s)
    await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct);

    // REVIEW FIX [R2-GPT8]: Update state IMMEDIATELY after HEADERS
    // If no body: HEADERS had END_STREAM → HalfClosedLocal
    // If body: HEADERS did NOT have END_STREAM → Open
    stream.State = hasBody ? Http2StreamState.Open : Http2StreamState.HalfClosedLocal;
}
finally
{
    _writeLock.Release();
}

// Step 7: Send DATA frames OUTSIDE the write lock (if body present)
// SendDataAsync acquires/releases _writeLock per-frame internally.
bool hasBody2 = request.Body != null && request.Body.Length > 0;
if (hasBody2)
{
    await SendDataAsync(streamId, request.Body, stream, ct);
    // After final DATA with END_STREAM: Open → HalfClosedLocal
    stream.State = Http2StreamState.HalfClosedLocal;
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

**REVIEW FIX [R2-1]:** `SendDataAsync` acquires and releases `_writeLock` per DATA frame, NOT for the entire body. This allows the read loop to interleave control frame writes (PING ACK, SETTINGS ACK, WINDOW_UPDATE) between DATA frames, preventing deadlock when the send window is exhausted.

```csharp
private async Task SendDataAsync(int streamId, byte[] body, Http2Stream stream, CancellationToken ct)
{
    int offset = 0;
    while (offset < body.Length)
    {
        // Wait for flow control window availability (OUTSIDE write lock)
        int available = Math.Min(
            Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0), // read
            stream.WindowSize);
        available = Math.Min(available, _remoteSettings.MaxFrameSize);
        available = Math.Min(available, body.Length - offset);

        if (available <= 0)
        {
            // Block until WINDOW_UPDATE received — NOT holding _writeLock
            await WaitForWindowUpdateAsync(ct);
            continue;
        }

        bool isLast = (offset + available) >= body.Length;
        var payload = new byte[available];
        Array.Copy(body, offset, payload, 0, available);

        // Acquire write lock per-frame (allows read loop to interleave control frames)
        await _writeLock.WaitAsync(ct);
        // REVIEW FIX [P3-1]: Track lock ownership to prevent double-release.
        // The early bail-out path (actualAvailable <= 0) releases in-line and sets
        // lockHeld = false so the finally block does not release again.
        bool lockHeld = true;
        try
        {
            // REVIEW FIX [P2-1]: Re-read windows after acquiring lock — other senders
            // may have consumed window between our read and lock acquisition.
            int connWindow = Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0);
            int streamWindow = stream.WindowSize;
            int actualAvailable = Math.Min(connWindow, streamWindow);
            actualAvailable = Math.Min(actualAvailable, _remoteSettings.MaxFrameSize);
            actualAvailable = Math.Min(actualAvailable, body.Length - offset);

            if (actualAvailable <= 0)
            {
                // Window consumed by another sender — release lock and retry
                _writeLock.Release();
                lockHeld = false;
                await WaitForWindowUpdateAsync(ct);
                continue;
            }

            // Rebuild payload if size changed
            if (actualAvailable != available)
            {
                payload = new byte[actualAvailable];
                Array.Copy(body, offset, payload, 0, actualAvailable);
                available = actualAvailable;
                isLast = (offset + available) >= body.Length;
            }

            // Decrement windows atomically before sending
            Interlocked.Add(ref _connectionSendWindow, -available);
            stream.AdjustWindowSize(-available); // REVIEW FIX [P2-2]: use public helper

            await _codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Data,
                Flags = isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                StreamId = streamId,
                Payload = payload,
                Length = available
            }, ct);
        }
        finally
        {
            if (lockHeld) _writeLock.Release();
        }

        offset += available;
    }
}
```

## `ReadLoopAsync` — Background Frame Dispatch

**REVIEW FIX [A3]:** Track `_continuationStreamId` to enforce CONTINUATION frame ordering per RFC 7540 Section 6.10. When expecting CONTINUATION, reject any other frame type (connection error PROTOCOL_ERROR).

```csharp
private int _continuationStreamId = 0; // 0 = not expecting CONTINUATION

private async Task ReadLoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await _codec.ReadFrameAsync(_remoteSettings.MaxFrameSize, ct);

            // REVIEW FIX [A3]: Enforce CONTINUATION ordering
            if (_continuationStreamId != 0 && frame.Type != Http2FrameType.Continuation)
            {
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    $"Expected CONTINUATION for stream {_continuationStreamId}, got {frame.Type}");
            }

            switch (frame.Type)
            {
                case Http2FrameType.Data:         await HandleDataFrameAsync(frame, ct); break;
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

### `HandleDataFrameAsync(Http2Frame frame, CancellationToken ct)`

**REVIEW FIX [GPT-4]:** Validate DATA doesn't exceed recv window (FLOW_CONTROL_ERROR).
**REVIEW FIX [GPT-5]:** Validate padding bounds.
**REVIEW FIX [GPT-1]:** Acquire `_writeLock` before sending WINDOW_UPDATE.

Note: Renamed to async because it sends WINDOW_UPDATE frames (which need write lock).

```
1. Validate frame.StreamId != 0 (connection error PROTOCOL_ERROR if 0)

2. REVIEW FIX [GPT-5]: Handle padding
   If Padded flag:
     padLength = frame.Payload[0]
     if padLength >= frame.Length → PROTOCOL_ERROR
     dataPayload = frame.Payload[1 .. frame.Length - padLength]
   Else:
     dataPayload = frame.Payload

3. REVIEW FIX [GPT-4]: Validate connection-level flow control
   dataLength = dataPayload.Length
   if dataLength > _connectionRecvWindow → FLOW_CONTROL_ERROR (connection error via GOAWAY)

4. Look up stream in _activeStreams
   If stream not found: send RST_STREAM(STREAM_CLOSED) — late frame after close

5. REVIEW FIX [P2-4]: Validate stream-level flow control
   if dataLength > stream recv window → FLOW_CONTROL_ERROR (stream error via RST_STREAM)

6. Append dataPayload to stream.ResponseBody

7. Decrement _connectionRecvWindow by dataLength
   Decrement stream recv window by dataLength
8. If _connectionRecvWindow < DefaultInitialWindowSize / 2:
   REVIEW FIX [GPT-1]: Acquire _writeLock before sending WINDOW_UPDATE
   await _writeLock.WaitAsync(ct);
   try { Send WINDOW_UPDATE on stream 0 (increment = consumed amount) } finally { _writeLock.Release(); }
   Reset _connectionRecvWindow

9. Similarly for stream-level recv window (with write lock for WINDOW_UPDATE)

10. If END_STREAM flag:
   If stream.HeadersReceived: stream.Complete()
   Remove from _activeStreams, dispose stream
```

### `HandleHeadersFrame(Http2Frame frame)`

**REVIEW FIX [GPT-5]:** Validate padding bounds (pad length >= frame length → PROTOCOL_ERROR).
**REVIEW FIX [A3]:** Track `_continuationStreamId` for CONTINUATION enforcement.

```
1. Validate frame.StreamId != 0
2. REVIEW FIX [A3]: If _continuationStreamId != 0 → PROTOCOL_ERROR (already caught in read loop)
3. Look up stream in _activeStreams

4. Parse payload with bounds validation:
   int payloadStart = 0;
   int payloadLength = frame.Length;

   REVIEW FIX [GPT-5]: Padding handling with bounds check
   If Padded flag:
     if frame.Length < 1 → PROTOCOL_ERROR
     byte padLength = frame.Payload[0]
     payloadStart = 1
     payloadLength -= (1 + padLength)
     if payloadLength < 0 → PROTOCOL_ERROR ("Pad length exceeds frame")

   If HasPriority flag:
     if payloadLength < 5 → PROTOCOL_ERROR
     payloadStart += 5  (skip 4 bytes dependency + 1 byte weight)
     payloadLength -= 5

5. Append frame.Payload[payloadStart .. payloadStart+payloadLength] to stream.HeaderBlockBuffer
   REVIEW FIX [R2-7]: Track END_STREAM on HEADERS for deferred completion (when headers span CONTINUATION)
   if END_STREAM flag: stream.PendingEndStream = true

6. If END_HEADERS flag:
   - Decode header block: _hpackDecoder.Decode(stream.GetHeaderBlock())
   - Extract :status pseudo-header → stream.StatusCode
   - Build HttpHeaders from remaining headers
   - Set stream.ResponseHeaders
   - Set stream.HeadersReceived = true
   - Clear stream.HeaderBlockBuffer
   Else:
   - REVIEW FIX [A3]: _continuationStreamId = frame.StreamId

7. If END_STREAM flag AND HeadersReceived:
   - stream.Complete()
   - Remove from _activeStreams
```

### `HandleContinuationFrame(Http2Frame frame)`

**REVIEW FIX [A3]:** Enforce CONTINUATION must match expected stream ID.

```
1. REVIEW FIX [A3]: Validate _continuationStreamId == frame.StreamId
   If _continuationStreamId == 0 → PROTOCOL_ERROR ("Unexpected CONTINUATION")
   If _continuationStreamId != frame.StreamId → PROTOCOL_ERROR ("Wrong stream")

2. Look up stream in _activeStreams
3. Append frame.Payload to stream.HeaderBlockBuffer
4. If END_HEADERS flag:
   - _continuationStreamId = 0  // REVIEW FIX [A3]: Sequence complete
   - Decode header block (same as HEADERS END_HEADERS handling)
5. REVIEW FIX [R2-7]: If stream.PendingEndStream AND now HeadersReceived:
   - stream.Complete()
   - Remove from _activeStreams, dispose stream
```

### `HandleSettingsFrameAsync(Http2Frame frame, CancellationToken ct)`

**REVIEW FIX [GPT-7]:** Validate SETTINGS ACK has zero payload.
**REVIEW FIX [GPT-1]:** Acquire `_writeLock` before sending SETTINGS ACK.

```
1. If ACK flag:
   - REVIEW FIX [GPT-7]: if frame.Length != 0 → FRAME_SIZE_ERROR
   - Signal _settingsAckTcs.TrySetResult(true)
   - Return
2. Validate frame.StreamId == 0 (connection error if not)
3. Parse settings: Http2Settings.ParsePayload(frame.Payload)
4. Save old InitialWindowSize
5. Apply each setting to _remoteSettings
6. If InitialWindowSize changed:
   - delta = newInitialWindowSize - oldInitialWindowSize
   - REVIEW FIX [R2-3]: For each active stream:
     long newWindow = (long)stream.WindowSize + delta
     if newWindow > int.MaxValue || newWindow < 0 → FLOW_CONTROL_ERROR (stream error)
     stream.AdjustWindowSize(delta)  // REVIEW FIX [P2-2]: use public helper
7. If HeaderTableSize changed:
   - _hpackEncoder.SetMaxDynamicTableSize(newSize)
8. REVIEW FIX [GPT-1]: Acquire _writeLock before sending SETTINGS ACK
   await _writeLock.WaitAsync(CancellationToken.None);
   try { Send SETTINGS ACK (empty payload, ACK flag, stream 0) }
   finally { _writeLock.Release(); }
```

### `HandlePingFrameAsync(Http2Frame frame, CancellationToken ct)`

**REVIEW FIX [GPT-1]:** Acquire `_writeLock` before sending PING ACK.

```
1. Validate frame.StreamId == 0
2. Validate frame.Payload.Length == 8 (FRAME_SIZE_ERROR if not)
3. If ACK flag: ignore (we don't send PINGs proactively in Phase 3)
4. If not ACK: echo back with ACK flag:
   REVIEW FIX [GPT-1]: Acquire _writeLock to prevent wire interleaving
   await _writeLock.WaitAsync(CancellationToken.None);
   try
   {
       await _codec.WriteFrameAsync(new Http2Frame
       {
           Type = Http2FrameType.Ping,
           Flags = Http2FrameFlags.Ack,
           StreamId = 0,
           Payload = frame.Payload  // Same 8 bytes
       }, CancellationToken.None);
   }
   finally { _writeLock.Release(); }
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

**REVIEW FIX [R2-3] (Critical):** Overflow check MUST use `long` arithmetic BEFORE the atomic add to prevent signed integer overflow. Signal `_windowWaiter` to wake blocked senders.

```
1. Validate frame.Payload.Length == 4 (FRAME_SIZE_ERROR)
2. Parse window increment (31-bit, mask reserved bit)
3. If increment == 0: PROTOCOL_ERROR (RFC 7540 Section 6.9.1)
4. If frame.StreamId == 0:
   - long newWindow = (long)Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0) + increment
   - if newWindow > int.MaxValue → FLOW_CONTROL_ERROR (connection error via GOAWAY)
   - Interlocked.Add(ref _connectionSendWindow, increment)
   - _windowWaiter.Release()  // Wake one blocked sender
5. If frame.StreamId > 0:
   - Look up stream
   - long newWindow = (long)stream.WindowSize + increment
   - if newWindow > int.MaxValue → FLOW_CONTROL_ERROR (stream error via RST_STREAM)
   - stream.AdjustWindowSize(increment)  // REVIEW FIX [P2-2]: use public helper
   - _windowWaiter.Release()  // Wake one blocked sender
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

**REVIEW FIX [R2-2]:** Fully specified signaling mechanism. Uses `SemaphoreSlim _windowWaiter(0)`. When a sender finds no available window, it calls `await _windowWaiter.WaitAsync(ct)`. When `HandleWindowUpdateFrame` processes a WINDOW_UPDATE, it calls `_windowWaiter.Release(Math.Max(1, _windowWaiter.CurrentCount == 0 ? 1 : 0))` to wake at least one waiting sender. Multiple concurrent senders will re-check windows in a loop and re-wait if still insufficient.

```csharp
private async Task WaitForWindowUpdateAsync(CancellationToken ct)
{
    // Blocks until HandleWindowUpdateFrame signals via _windowWaiter.Release()
    // NOT holding _writeLock — allows read loop to process frames freely.
    await _windowWaiter.WaitAsync(ct);
}
```

### `SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)`

**REVIEW FIX [GPT-1]:** Must acquire `_writeLock` before writing. Called from read loop (on protocol errors) and from cancellation callbacks (from arbitrary threads).

**REVIEW FIX [R2-5]:** When called fire-and-forget from cancellation callbacks, wrap in try/catch to swallow `ObjectDisposedException` during connection disposal.

```csharp
private async Task SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)
{
    try
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            // ... build and send RST_STREAM frame ...
        }
        finally { _writeLock.Release(); }
    }
    catch (ObjectDisposedException) { /* Connection being disposed, ignore */ }
}
```

### `FailAllStreams(Exception ex)`

**REVIEW FIX [R2-4]:** Set `_goawayReceived = true` BEFORE iterating to prevent new streams from being added by concurrent `SendRequestAsync` callers. `SendRequestAsync` checks `_goawayReceived` in step 1 (pre-flight) and also re-checks after adding to `_activeStreams`.

```
1. _goawayReceived = true  // Prevent new streams
2. _cts.Cancel()           // Signal shutdown
3. Iterate _activeStreams: for each stream, call stream.Fail(ex)
4. Clear _activeStreams
```

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

    // REVIEW FIX [A4]: Http2Connection owns the stream after TransferOwnership.
    // Dispose it here. The connection manager called TransferOwnership on the lease,
    // so the pool won't try to dispose it.
    _stream?.Dispose();
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
| `_codec.WriteFrameAsync` | `_writeLock` serializes **ALL writes** | Multiple streams + read loop (PING ACK, SETTINGS ACK, WINDOW_UPDATE, RST_STREAM) — REVIEW FIX [GPT-1] |
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
- [ ] REVIEW: All frame writes acquire `_writeLock` (including control frames from read loop)
- [ ] REVIEW: DATA exceeding recv window triggers FLOW_CONTROL_ERROR
- [ ] REVIEW: Padding bounds validated (pad length < frame length)
- [ ] REVIEW: SETTINGS ACK with non-zero payload triggers FRAME_SIZE_ERROR
- [ ] REVIEW: CONTINUATION only accepted when expected, on correct stream
- [ ] REVIEW: Non-CONTINUATION frame while expecting CONTINUATION → PROTOCOL_ERROR
- [ ] REVIEW: Stream state correctly transitions Idle → Open for body-bearing requests
- [ ] REVIEW: Early cancellation (ct already cancelled) doesn't leak stream
- [ ] REVIEW: Dispose disposes `_stream` (owned after TransferOwnership)
- [ ] REVIEW R2: Write lock released between HEADERS and DATA sends (prevents deadlock)
- [ ] REVIEW R2: SendDataAsync acquires/releases _writeLock per DATA frame
- [ ] REVIEW R2: WaitForWindowUpdateAsync does NOT hold _writeLock
- [ ] REVIEW R2: Window overflow checked with long arithmetic before Interlocked.Add
- [ ] REVIEW R2: FailAllStreams sets _goawayReceived before clearing (race prevention)
- [ ] REVIEW R2: SendRequestAsync re-checks shutdown after adding to _activeStreams
- [ ] REVIEW R2: Stream ID overflow guarded (> int.MaxValue)
- [ ] REVIEW R2: HEADERS END_STREAM tracked via _pendingEndStream for CONTINUATION
- [ ] REVIEW R2: SendRstStreamAsync handles ObjectDisposedException in fire-and-forget path
