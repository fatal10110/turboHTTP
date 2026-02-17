# Step 3B.2: HTTP/2 Frame Reader/Writer

**File:** `Runtime/Transport/Http2/Http2FrameCodec.cs`
**Depends on:** Step 3B.1 (Http2Frame, Http2Constants)
**Spec:** RFC 7540 Section 4.1 (Frame Format), Section 3.5 (Connection Preface)

## Purpose

Read and write HTTP/2 frames from/to a `System.IO.Stream` (the TLS stream). Handles the 9-byte frame header encoding/decoding and payload I/O.

## Class Design

```csharp
namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Reads and writes HTTP/2 frames from/to a stream.
    /// NOT thread-safe — callers must serialize access (write lock for writes,
    /// single read loop for reads).
    /// </summary>
    internal class Http2FrameCodec
    {
        private readonly Stream _stream;

        public Http2FrameCodec(Stream stream);
        public Task<Http2Frame> ReadFrameAsync(int maxFrameSize, CancellationToken ct);
        public Task WriteFrameAsync(Http2Frame frame, CancellationToken ct);
        public Task WritePrefaceAsync(CancellationToken ct);
        private Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct);
    }
}
```

## Method Specifications

### `ReadFrameAsync(int maxFrameSize, CancellationToken ct)`

1. Allocate 9-byte buffer for the frame header.
2. Call `ReadExactAsync(header, 9, ct)` to read the full header.
3. Decode the header:
   ```csharp
   Length   = (header[0] << 16) | (header[1] << 8) | header[2]
   Type     = (Http2FrameType)header[3]
   Flags    = (Http2FrameFlags)header[4]
   StreamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) |
              (header[7] << 8) | header[8]
   ```
4. **Validate frame size:** If `Length > maxFrameSize`, throw `Http2ProtocolException` with `FrameSizeError`. This is a connection error per RFC 7540 Section 4.2.
5. Read payload:
   - If `Length > 0`: allocate `byte[Length]`, call `ReadExactAsync(payload, Length, ct)`.
   - If `Length == 0`: set `Payload = Array.Empty<byte>()`.
6. Return the populated `Http2Frame`.

### `WriteFrameAsync(Http2Frame frame, CancellationToken ct)`

1. Compute `payloadLength = frame.Payload?.Length ?? 0`.
2. Encode 9-byte header:
   ```csharp
   header[0] = (byte)((payloadLength >> 16) & 0xFF)
   header[1] = (byte)((payloadLength >>  8) & 0xFF)
   header[2] = (byte)( payloadLength        & 0xFF)
   header[3] = (byte)frame.Type
   header[4] = (byte)frame.Flags
   header[5] = (byte)((frame.StreamId >> 24) & 0x7F)  // mask reserved bit
   header[6] = (byte)((frame.StreamId >> 16) & 0xFF)
   header[7] = (byte)((frame.StreamId >>  8) & 0xFF)
   header[8] = (byte)( frame.StreamId        & 0xFF)
   ```
3. Write header to stream: `_stream.WriteAsync(header, 0, 9, ct)`.
4. If `payloadLength > 0`: write payload: `_stream.WriteAsync(frame.Payload, 0, payloadLength, ct)`.
5. Flush: `_stream.FlushAsync(ct)`.

### `WritePrefaceAsync(CancellationToken ct)`

Write the 24-byte connection preface (`Http2Constants.ConnectionPreface`):
```csharp
await _stream.WriteAsync(Http2Constants.ConnectionPreface, 0,
    Http2Constants.ConnectionPreface.Length, ct);
await _stream.FlushAsync(ct);
```

### `ReadExactAsync(byte[] buffer, int count, CancellationToken ct)` (private)

Loop `ReadAsync` until exactly `count` bytes are read. If `ReadAsync` returns 0 before `count` bytes, throw `IOException("Unexpected end of HTTP/2 stream")`.

```csharp
int offset = 0;
while (offset < count)
{
    int read = await _stream.ReadAsync(buffer, offset, count - offset, ct);
    if (read == 0)
        throw new IOException("Unexpected end of HTTP/2 stream");
    offset += read;
}
```

## Thread Safety

The codec is NOT thread-safe. This is by design:
- **Reads:** Only the single background read loop (`Http2Connection.ReadLoopAsync`) calls `ReadFrameAsync`. No concurrent readers.
- **Writes:** `Http2Connection` uses a `SemaphoreSlim(1,1)` write lock to serialize all `WriteFrameAsync` calls from multiple streams.

## Performance Notes (Phase 3 — Correctness Focus)

- Allocates a fresh `byte[9]` per read/write call. Phase 10 will pool this buffer.
- Allocates a fresh `byte[Length]` per frame payload. Phase 10 will use `ArrayPool<byte>`.
- `FlushAsync` called per frame write. Could batch for performance in Phase 10.

## Error Handling

| Condition | Exception | Error Code |
|-----------|-----------|------------|
| Unexpected EOF during header/payload read | `IOException` | — |
| Frame length exceeds `maxFrameSize` | Connection error | `FrameSizeError` |
| `stream` is null in constructor | `ArgumentNullException` | — |

## Validation Criteria

- [ ] Frame round-trip: write a frame → read it back → all fields match
- [ ] All 10 frame types can be written and read
- [ ] Stream ID high bit is always masked (cleared) on read
- [ ] Stream ID high bit is always masked (cleared) on write
- [ ] Zero-payload frames (SETTINGS ACK, PING) produce `Payload = Array.Empty<byte>()`
- [ ] Frame size validation rejects oversized frames
- [ ] Connection preface is exactly 24 bytes
