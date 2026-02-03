# Step 3B.10: HTTP/2 Stream State Machine

**File:** `Runtime/Transport/Http2/Http2Stream.cs`
**Depends on:** Steps 3B.1 (Http2Frame), 3B.9 (Http2Settings)
**Spec:** RFC 7540 Section 5.1 (Stream States)

## Purpose

Represent a single HTTP/2 stream — one request/response pair multiplexed on a shared TCP connection. Each stream tracks its state, accumulates response data, and signals completion via a `TaskCompletionSource`.

## Types

### `Http2StreamState` Enum

```csharp
internal enum Http2StreamState
{
    Idle,              // Stream allocated but no frames sent/received
    Open,              // HEADERS sent, waiting for response
    HalfClosedLocal,   // We sent END_STREAM (request complete), waiting for response
    HalfClosedRemote,  // Server sent END_STREAM, we may still send (uncommon for client)
    Closed             // Both sides done, or RST_STREAM received
}
```

Simplified from RFC 7540 Section 5.1 — we omit `ReservedLocal`/`ReservedRemote` since we don't implement PUSH_PROMISE.

### `Http2Stream` Class

```csharp
namespace TurboHTTP.Transport.Http2
{
    internal class Http2Stream : IDisposable
    {
        // Identity
        public int StreamId { get; }
        public UHttpRequest Request { get; }
        public RequestContext Context { get; }

        // State
        public Http2StreamState State { get; set; }

        // Response accumulation
        public int StatusCode { get; set; }
        public HttpHeaders ResponseHeaders { get; set; }
        public MemoryStream ResponseBody { get; }

        // Header block accumulation (for HEADERS + CONTINUATION spanning)
        public List<byte> HeaderBlockBuffer { get; }
        public bool HeadersReceived { get; set; }

        // Flow control
        public int WindowSize { get; set; }

        // Completion signal
        public TaskCompletionSource<UHttpResponse> ResponseTcs { get; }

        // Cancellation
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        // Constructor, Complete, Fail, Dispose
    }
}
```

## Constructor

```csharp
public Http2Stream(int streamId, UHttpRequest request, RequestContext context, int initialWindowSize)
{
    StreamId = streamId;
    Request = request;
    Context = context;
    State = Http2StreamState.Idle;
    StatusCode = 0;
    ResponseBody = new MemoryStream();
    HeaderBlockBuffer = new List<byte>();
    HeadersReceived = false;
    WindowSize = initialWindowSize;

    // RunContinuationsAsynchronously prevents deadlocks:
    // Without it, the continuation (caller's await) runs synchronously
    // on the read loop thread, which could block frame processing.
    ResponseTcs = new TaskCompletionSource<UHttpResponse>(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
```

## State Transitions

```
Client sends HEADERS (no END_STREAM):     Idle → Open
Client sends HEADERS (with END_STREAM):   Idle → HalfClosedLocal
Client sends DATA with END_STREAM:        Open → HalfClosedLocal
Server sends HEADERS/DATA with END_STREAM on Open:            Open → Closed
Server sends HEADERS/DATA with END_STREAM on HalfClosedLocal: HalfClosedLocal → Closed
RST_STREAM received:                      Any → Closed
RST_STREAM sent:                          Any → Closed
```

For a typical GET request (no body):
```
Idle → HalfClosedLocal (HEADERS with END_STREAM)
     → Closed (server HEADERS+DATA with END_STREAM → Complete())
```

For a POST request (with body):
```
Idle → Open (HEADERS without END_STREAM)
     → HalfClosedLocal (DATA with END_STREAM)
     → Closed (server HEADERS+DATA with END_STREAM → Complete())
```

## `Complete()` Method

Called when the response is fully received (END_STREAM on the last server frame):

```csharp
public void Complete()
{
    State = Http2StreamState.Closed;

    var response = new UHttpResponse(
        statusCode: (System.Net.HttpStatusCode)StatusCode,
        headers: ResponseHeaders ?? new HttpHeaders(),
        body: ResponseBody.ToArray(),
        elapsedTime: Context.Elapsed,
        request: Request,
        error: null
    );

    ResponseTcs.TrySetResult(response);
}
```

**Note:** Uses `TrySetResult` (not `SetResult`) because the stream may have been cancelled or failed concurrently.

## `Fail(Exception exception)` Method

Called on RST_STREAM, GOAWAY, or cancellation:

```csharp
public void Fail(Exception exception)
{
    State = Http2StreamState.Closed;
    ResponseTcs.TrySetException(exception);
}

public void Cancel()
{
    State = Http2StreamState.Closed;
    ResponseTcs.TrySetCanceled();
}
```

## `Dispose()` Method

```csharp
public void Dispose()
{
    CancellationRegistration.Dispose();
    ResponseBody?.Dispose();
}
```

## Header Block Accumulation

HTTP/2 headers may span multiple frames (HEADERS + CONTINUATION). The `HeaderBlockBuffer` accumulates the raw HPACK-encoded bytes until `END_HEADERS` is received.

```csharp
// In Http2Connection:
public void AppendHeaderBlock(byte[] data, int offset, int length)
{
    // Using List<byte> for simplicity in Phase 3
    for (int i = offset; i < offset + length; i++)
        HeaderBlockBuffer.Add(data[i]);
}

public byte[] GetHeaderBlock()
{
    return HeaderBlockBuffer.ToArray();
}
```

**Phase 10 optimization:** Replace `List<byte>` with `ArrayPool<byte>`-backed buffers.

## Flow Control

Each stream has its own send window (`WindowSize`), initialized from `INITIAL_WINDOW_SIZE` in server settings. The window is:
- **Decremented** by the connection when sending DATA frames for this stream
- **Incremented** by stream-level WINDOW_UPDATE frames from the server
- **Adjusted** when the server changes `INITIAL_WINDOW_SIZE` via SETTINGS (delta applied to all active streams)

The flow control blocking/signaling logic lives in `Http2Connection`, not in `Http2Stream`. The stream just stores the current window size.

## Thread Safety

`Http2Stream` is NOT independently thread-safe, but access is coordinated by `Http2Connection`:
- **Write-side fields** (State during send, WindowSize during DATA): accessed under `_writeLock`.
- **Read-side fields** (ResponseHeaders, ResponseBody, HeaderBlockBuffer, State during receive): accessed from the single read loop thread.
- **ResponseTcs**: Thread-safe by design (`TrySetResult`/`TrySetException` are thread-safe).
- **WindowSize**: May be read by write thread and written by read thread (WINDOW_UPDATE). Use `Interlocked` or explicit synchronization in `Http2Connection`.

## `TaskCreationOptions.RunContinuationsAsynchronously` — Why It Matters

Without this flag, when `ResponseTcs.TrySetResult()` is called from the read loop, the awaiting caller's continuation runs **synchronously on the read loop thread**. If that continuation does blocking work or tries to send another request (acquiring the write lock), it deadlocks the read loop.

With `RunContinuationsAsynchronously`, the continuation is posted to the ThreadPool, keeping the read loop free to process more frames.

## Validation Criteria

- [ ] Constructor initializes all fields correctly
- [ ] State transitions follow RFC 7540 Section 5.1
- [ ] `Complete()` builds `UHttpResponse` with correct status code, headers, body
- [ ] `Complete()` calls `TrySetResult` (not `SetResult`)
- [ ] `Fail()` calls `TrySetException`
- [ ] `Cancel()` calls `TrySetCanceled`
- [ ] `ResponseTcs` uses `RunContinuationsAsynchronously`
- [ ] `Dispose()` disposes `MemoryStream` and `CancellationTokenRegistration`
- [ ] Header block accumulation works across multiple appends
- [ ] WindowSize is initialized from settings
