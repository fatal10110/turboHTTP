# Phase 3B: HTTP/2 Protocol Implementation

**Milestone:** M0 (Spike)
**Dependencies:** Phase 3 (Client API & HTTP/1.1 Raw Socket Transport)
**Estimated Complexity:** Very High
**Critical:** Yes - Key differentiator over UnityWebRequest and competitors

## Overview

Implement HTTP/2 protocol support on top of the raw socket + TLS infrastructure from Phase 3. This includes the HTTP/2 binary framing layer, HPACK header compression, stream multiplexing, flow control, and ALPN-based protocol negotiation during the TLS handshake. The `RawSocketTransport` is updated to automatically select HTTP/2 or HTTP/1.1 based on server support.

## Goals

1. Implement HTTP/2 binary frame reader/writer (9-byte frame header + payload)
2. Implement HPACK header compression (static table, dynamic table, Huffman encoding)
3. Implement HTTP/2 stream multiplexing (concurrent requests over a single TCP connection)
4. Implement HTTP/2 flow control (connection-level and stream-level window updates)
5. Handle HTTP/2 connection lifecycle (SETTINGS, PING, GOAWAY frames)
6. Integrate ALPN negotiation into `TlsStreamWrapper` to select h2 vs http/1.1
7. Update `RawSocketTransport` to route to HTTP/2 or HTTP/1.1 based on negotiated protocol

## Tasks

### Task 3B.1: HTTP/2 Frame Types and Constants

**File:** `Runtime/Transport/Http2/Http2Frame.cs`

```csharp
using System;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HTTP/2 frame types as defined in RFC 7540 Section 6.
    /// </summary>
    public enum Http2FrameType : byte
    {
        Data         = 0x0,
        Headers      = 0x1,
        Priority     = 0x2,
        RstStream    = 0x3,
        Settings     = 0x4,
        PushPromise  = 0x5,
        Ping         = 0x6,
        GoAway       = 0x7,
        WindowUpdate = 0x8,
        Continuation = 0x9
    }

    /// <summary>
    /// HTTP/2 frame flags (commonly used ones).
    /// </summary>
    [Flags]
    public enum Http2FrameFlags : byte
    {
        None        = 0x0,
        EndStream   = 0x1,  // DATA, HEADERS
        Ack         = 0x1,  // SETTINGS, PING
        EndHeaders  = 0x4,  // HEADERS, CONTINUATION
        Padded      = 0x8,  // DATA, HEADERS
        HasPriority = 0x20  // HEADERS
    }

    /// <summary>
    /// Represents a single HTTP/2 frame (9-byte header + payload).
    /// </summary>
    public class Http2Frame
    {
        /// <summary>Length of the frame payload (24 bits, max 16384 default).</summary>
        public int Length { get; set; }

        /// <summary>Frame type.</summary>
        public Http2FrameType Type { get; set; }

        /// <summary>Frame flags.</summary>
        public Http2FrameFlags Flags { get; set; }

        /// <summary>Stream identifier (31 bits, 0 = connection-level).</summary>
        public int StreamId { get; set; }

        /// <summary>Frame payload bytes.</summary>
        public byte[] Payload { get; set; }

        public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) != 0;
    }

    /// <summary>
    /// HTTP/2 error codes as defined in RFC 7540 Section 7.
    /// </summary>
    public enum Http2ErrorCode : uint
    {
        NoError            = 0x0,
        ProtocolError      = 0x1,
        InternalError      = 0x2,
        FlowControlError   = 0x3,
        SettingsTimeout    = 0x4,
        StreamClosed       = 0x5,
        FrameSizeError     = 0x6,
        RefusedStream      = 0x7,
        Cancel             = 0x8,
        CompressionError   = 0x9,
        ConnectError       = 0xa,
        EnhanceYourCalm    = 0xb,
        InadequateSecurity = 0xc,
        Http11Required     = 0xd
    }

    /// <summary>
    /// HTTP/2 settings identifiers as defined in RFC 7540 Section 6.5.2.
    /// </summary>
    public enum Http2SettingId : ushort
    {
        HeaderTableSize      = 0x1,
        EnablePush           = 0x2,
        MaxConcurrentStreams  = 0x3,
        InitialWindowSize    = 0x4,
        MaxFrameSize         = 0x5,
        MaxHeaderListSize    = 0x6
    }
}
```

**Notes:**
- All frame types, flags, error codes, and settings from RFC 7540
- Frame structure: 3 bytes length + 1 byte type + 1 byte flags + 4 bytes stream ID + payload
- Max default frame payload is 16384 bytes (configurable via SETTINGS)

### Task 3B.2: HTTP/2 Frame Reader/Writer

**File:** `Runtime/Transport/Http2/Http2FrameCodec.cs`

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Reads and writes HTTP/2 frames from/to a stream.
    /// Handles the 9-byte frame header encoding/decoding.
    /// </summary>
    public class Http2FrameCodec
    {
        private readonly Stream _stream;

        public Http2FrameCodec(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// Read a single HTTP/2 frame from the stream.
        /// </summary>
        public async Task<Http2Frame> ReadFrameAsync(CancellationToken ct = default)
        {
            // Read 9-byte frame header
            var header = new byte[9];
            await ReadExactAsync(header, 9, ct);

            var frame = new Http2Frame
            {
                Length   = (header[0] << 16) | (header[1] << 8) | header[2],
                Type     = (Http2FrameType)header[3],
                Flags    = (Http2FrameFlags)header[4],
                StreamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) |
                           (header[7] << 8) | header[8]
            };

            // Read payload
            if (frame.Length > 0)
            {
                frame.Payload = new byte[frame.Length];
                await ReadExactAsync(frame.Payload, frame.Length, ct);
            }
            else
            {
                frame.Payload = Array.Empty<byte>();
            }

            return frame;
        }

        /// <summary>
        /// Write a single HTTP/2 frame to the stream.
        /// </summary>
        public async Task WriteFrameAsync(Http2Frame frame, CancellationToken ct = default)
        {
            var payloadLength = frame.Payload?.Length ?? 0;

            var header = new byte[9];
            header[0] = (byte)((payloadLength >> 16) & 0xFF);
            header[1] = (byte)((payloadLength >> 8) & 0xFF);
            header[2] = (byte)(payloadLength & 0xFF);
            header[3] = (byte)frame.Type;
            header[4] = (byte)frame.Flags;
            header[5] = (byte)((frame.StreamId >> 24) & 0x7F);
            header[6] = (byte)((frame.StreamId >> 16) & 0xFF);
            header[7] = (byte)((frame.StreamId >> 8) & 0xFF);
            header[8] = (byte)(frame.StreamId & 0xFF);

            await _stream.WriteAsync(header, 0, 9, ct);

            if (payloadLength > 0)
            {
                await _stream.WriteAsync(frame.Payload, 0, payloadLength, ct);
            }

            await _stream.FlushAsync(ct);
        }

        private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await _stream.ReadAsync(buffer, offset, count - offset, ct);
                if (read == 0)
                    throw new IOException("Unexpected end of HTTP/2 stream");
                offset += read;
            }
        }
    }
}
```

**Notes:**
- 9-byte frame header: 3 bytes length (24-bit) + 1 type + 1 flags + 4 stream ID (31-bit, high bit reserved)
- Stream ID 0 = connection-level frames (SETTINGS, PING, GOAWAY, WINDOW_UPDATE)
- Payload length is limited by SETTINGS_MAX_FRAME_SIZE (default 16384)

### Task 3B.3: HPACK Header Compression

**File:** `Runtime/Transport/Http2/HpackEncoder.cs` and `Runtime/Transport/Http2/HpackDecoder.cs`

This is the most complex component. HPACK (RFC 7541) compresses HTTP headers using:

1. **Static table:** 61 predefined header name/value pairs
2. **Dynamic table:** Recently seen headers (bounded size)
3. **Huffman encoding:** Variable-length encoding for string literals

```csharp
namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HPACK header encoder (RFC 7541).
    /// Encodes HttpHeaders into compressed binary format for HTTP/2 HEADERS frames.
    /// </summary>
    public class HpackEncoder
    {
        private readonly HpackDynamicTable _dynamicTable;

        public HpackEncoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
        }

        /// <summary>
        /// Encode headers into HPACK binary format.
        /// </summary>
        public byte[] Encode(TurboHTTP.Core.HttpHeaders headers)
        {
            // Implementation:
            // 1. For each header, check static table for indexed match
            // 2. If found, emit indexed header field (1 byte for common headers)
            // 3. If name found but value differs, emit literal with indexed name
            // 4. If not found, emit literal with new name
            // 5. Optionally add to dynamic table
            // 6. Apply Huffman encoding to string literals
            throw new System.NotImplementedException("Full HPACK implementation required");
        }
    }

    /// <summary>
    /// HPACK header decoder (RFC 7541).
    /// Decodes HPACK binary format back into HttpHeaders.
    /// </summary>
    public class HpackDecoder
    {
        private readonly HpackDynamicTable _dynamicTable;

        public HpackDecoder(int maxDynamicTableSize = 4096)
        {
            _dynamicTable = new HpackDynamicTable(maxDynamicTableSize);
        }

        /// <summary>
        /// Decode HPACK binary format into headers.
        /// </summary>
        public TurboHTTP.Core.HttpHeaders Decode(byte[] data)
        {
            // Implementation:
            // 1. Read first byte to determine representation type
            // 2. Indexed Header Field (bit 7 set): lookup in static/dynamic table
            // 3. Literal with Incremental Indexing (bits 6 set): decode and add to dynamic table
            // 4. Literal without Indexing (bits 4 set): decode, don't add to table
            // 5. Literal Never Indexed (bits 4 set, different prefix): sensitive headers
            // 6. Dynamic Table Size Update: adjust dynamic table max size
            throw new System.NotImplementedException("Full HPACK implementation required");
        }
    }

    /// <summary>
    /// HPACK dynamic table — FIFO with bounded byte size.
    /// </summary>
    public class HpackDynamicTable
    {
        private readonly int _maxSize;
        // Entries stored as (name, value) pairs
        // Total size = sum of (name.Length + value.Length + 32) for each entry

        public HpackDynamicTable(int maxSize)
        {
            _maxSize = maxSize;
        }

        // Add, lookup, evict methods...
    }

    /// <summary>
    /// HPACK static table — 61 predefined header entries (RFC 7541 Appendix A).
    /// </summary>
    public static class HpackStaticTable
    {
        // Index 1: ":authority" -> ""
        // Index 2: ":method" -> "GET"
        // Index 3: ":method" -> "POST"
        // Index 4: ":path" -> "/"
        // Index 5: ":path" -> "/index.html"
        // Index 6: ":scheme" -> "http"
        // Index 7: ":scheme" -> "https"
        // ... (61 total entries)
    }

    /// <summary>
    /// Huffman encoding/decoding for HPACK string literals (RFC 7541 Appendix B).
    /// </summary>
    public static class HpackHuffman
    {
        // 256-entry encoding table + decoding tree
        // Encode: map each byte to variable-length bit sequence
        // Decode: walk Huffman tree bit-by-bit
    }
}
```

**Notes:**
- HPACK is the most implementation-heavy part of HTTP/2
- Static table has 61 entries covering common headers (`:method`, `:path`, `:status`, `content-type`, etc.)
- Dynamic table is per-connection and bounded (default 4096 bytes)
- Huffman encoding reduces header sizes by ~30% for typical headers
- `allowUnsafeCode` in the Transport asmdef enables efficient bit manipulation for Huffman

### Task 3B.4: HTTP/2 Stream and Connection Management

**File:** `Runtime/Transport/Http2/Http2Connection.cs`

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Represents a single HTTP/2 stream (one request/response pair).
    /// </summary>
    internal class Http2Stream
    {
        public int StreamId { get; }
        public TaskCompletionSource<UHttpResponse> ResponseTcs { get; }
        public HttpHeaders ResponseHeaders { get; set; }
        public MemoryStream ResponseBody { get; }
        public UHttpRequest Request { get; }
        public RequestContext Context { get; }

        public Http2Stream(int streamId, UHttpRequest request, RequestContext context)
        {
            StreamId = streamId;
            Request = request;
            Context = context;
            ResponseTcs = new TaskCompletionSource<UHttpResponse>();
            ResponseBody = new MemoryStream();
        }
    }

    /// <summary>
    /// Manages a single HTTP/2 connection with multiplexed streams.
    /// Handles frame dispatch, flow control, and connection lifecycle.
    /// </summary>
    public class Http2Connection : IDisposable
    {
        private readonly Http2FrameCodec _codec;
        private readonly HpackEncoder _hpackEncoder;
        private readonly HpackDecoder _hpackDecoder;
        private readonly ConcurrentDictionary<int, Http2Stream> _activeStreams;
        private readonly SemaphoreSlim _writeLock;

        private int _nextStreamId = 1; // Client streams are odd-numbered
        private int _connectionWindowSize = 65535; // Default initial window
        private int _maxConcurrentStreams = 100;    // Server may adjust via SETTINGS
        private int _maxFrameSize = 16384;          // Server may adjust via SETTINGS

        private Task _readLoopTask;
        private CancellationTokenSource _cts;

        public Http2Connection(Stream stream)
        {
            _codec = new Http2FrameCodec(stream);
            _hpackEncoder = new HpackEncoder();
            _hpackDecoder = new HpackDecoder();
            _activeStreams = new ConcurrentDictionary<int, Http2Stream>();
            _writeLock = new SemaphoreSlim(1, 1);
            _cts = new CancellationTokenSource();
        }

        /// <summary>
        /// Initialize the HTTP/2 connection: send connection preface and initial SETTINGS.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct = default)
        {
            // 1. Send connection preface: "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"
            // 2. Send initial SETTINGS frame
            // 3. Start background read loop
            // 4. Wait for server SETTINGS + ACK
        }

        /// <summary>
        /// Send a request on this connection as a new HTTP/2 stream.
        /// Returns when the complete response is received.
        /// </summary>
        public async Task<UHttpResponse> SendRequestAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken ct = default)
        {
            // 1. Allocate stream ID (odd, incrementing)
            var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
            var stream = new Http2Stream(streamId, request, context);
            _activeStreams[streamId] = stream;

            // 2. Encode headers with HPACK
            // 3. Send HEADERS frame (+ CONTINUATION if headers exceed max frame size)
            // 4. Send DATA frames for body (respecting flow control window)
            // 5. Wait for response via TaskCompletionSource

            return await stream.ResponseTcs.Task;
        }

        /// <summary>
        /// Background loop that reads frames from the connection
        /// and dispatches them to the appropriate stream.
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await _codec.ReadFrameAsync(ct);

                switch (frame.Type)
                {
                    case Http2FrameType.Data:
                        HandleDataFrame(frame);
                        break;
                    case Http2FrameType.Headers:
                        HandleHeadersFrame(frame);
                        break;
                    case Http2FrameType.Settings:
                        await HandleSettingsFrameAsync(frame, ct);
                        break;
                    case Http2FrameType.Ping:
                        await HandlePingFrameAsync(frame, ct);
                        break;
                    case Http2FrameType.GoAway:
                        HandleGoAwayFrame(frame);
                        break;
                    case Http2FrameType.WindowUpdate:
                        HandleWindowUpdateFrame(frame);
                        break;
                    case Http2FrameType.RstStream:
                        HandleRstStreamFrame(frame);
                        break;
                }
            }
        }

        // Frame handlers:
        // - DATA: append payload to stream's response body, send WINDOW_UPDATE
        // - HEADERS: decode via HPACK, set response headers on stream
        // - SETTINGS: update connection parameters, send ACK
        // - PING: echo back with ACK flag
        // - GOAWAY: close connection gracefully, fail pending streams
        // - WINDOW_UPDATE: increase flow control window
        // - RST_STREAM: fail specific stream with error

        private void HandleDataFrame(Http2Frame frame) { /* ... */ }
        private void HandleHeadersFrame(Http2Frame frame) { /* ... */ }
        private Task HandleSettingsFrameAsync(Http2Frame frame, CancellationToken ct) { return Task.CompletedTask; }
        private Task HandlePingFrameAsync(Http2Frame frame, CancellationToken ct) { return Task.CompletedTask; }
        private void HandleGoAwayFrame(Http2Frame frame) { /* ... */ }
        private void HandleWindowUpdateFrame(Http2Frame frame) { /* ... */ }
        private void HandleRstStreamFrame(Http2Frame frame) { /* ... */ }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _writeLock?.Dispose();
        }
    }
}
```

**Notes:**
- Client stream IDs are odd and incrementing (1, 3, 5, 7...)
- Background read loop dispatches frames to streams by stream ID
- Flow control requires sending WINDOW_UPDATE frames as data is consumed
- Connection preface is a fixed 24-byte magic string followed by SETTINGS frame
- `SemaphoreSlim` serializes frame writes (multiple streams share one TCP connection)
- GOAWAY triggers graceful shutdown — no new streams, wait for existing to complete

### Task 3B.5: Update RawSocketTransport for HTTP/2

**File:** `Runtime/Transport/RawSocketTransport.cs` (update)

Update `RawSocketTransport` to:

1. Pass ALPN protocols `["h2", "http/1.1"]` to `TlsStreamWrapper.WrapAsync()`
2. After TLS handshake, check `TlsStreamWrapper.GetNegotiatedProtocol()`
3. If `"h2"`: create/reuse `Http2Connection`, call `SendRequestAsync()`
4. If `"http/1.1"` or no ALPN: use existing HTTP/1.1 path from Phase 3

```csharp
// Pseudocode for the updated SendAsync:
public async Task<UHttpResponse> SendAsync(...)
{
    var connection = await GetOrCreateConnectionAsync(host, port, secure, ct);

    if (connection.NegotiatedProtocol == "h2")
    {
        // HTTP/2 path: multiplex on shared connection
        var h2Conn = GetOrCreateHttp2Connection(connection);
        return await h2Conn.SendRequestAsync(request, context, ct);
    }
    else
    {
        // HTTP/1.1 path: serialize/parse on dedicated connection
        await Http11RequestSerializer.SerializeAsync(request, connection.Stream, ct);
        var parsed = await Http11ResponseParser.ParseAsync(connection.Stream, ct);
        // ... (existing logic)
    }
}
```

**Notes:**
- HTTP/2 connections are long-lived and shared (one per host:port)
- HTTP/1.1 connections are pooled per-request (keep-alive)
- Automatic fallback: if server doesn't support h2, ALPN negotiates http/1.1
- Plain HTTP (non-TLS) always uses HTTP/1.1 (h2c upgrade is not implemented in v1.0)

## Validation Criteria

### Success Criteria

- [ ] HTTP/2 connection preface sent correctly
- [ ] SETTINGS frames exchanged and acknowledged
- [ ] HPACK encodes/decodes headers correctly (test against known vectors from RFC 7541)
- [ ] Can make GET request over HTTP/2 and receive response
- [ ] Can make POST request with body over HTTP/2
- [ ] Stream multiplexing works (multiple concurrent requests on one connection)
- [ ] Flow control window updates sent correctly
- [ ] PING frames echoed with ACK
- [ ] GOAWAY handled gracefully
- [ ] RST_STREAM fails individual streams without killing connection
- [ ] ALPN negotiation selects h2 when server supports it
- [ ] Automatic fallback to HTTP/1.1 when server doesn't support h2
- [ ] No regression on HTTP/1.1 behavior from Phase 3

### Unit Tests

```csharp
// Tests/Runtime/Transport/Http2/Http2FrameCodecTests.cs
// - Test frame serialization/deserialization round-trip
// - Test all frame types
// - Test stream ID encoding (31-bit, high bit reserved)

// Tests/Runtime/Transport/Http2/HpackTests.cs
// - Test static table lookups
// - Test dynamic table add/evict
// - Test encoding/decoding round-trip
// - Test RFC 7541 Appendix C test vectors (known inputs/outputs)
// - Test Huffman encoding/decoding

// Tests/Runtime/Transport/Http2/Http2ConnectionTests.cs
// - Test connection preface
// - Test SETTINGS exchange
// - Test stream lifecycle (HEADERS -> DATA -> END_STREAM)
// - Test multiplexing (concurrent streams)
// - Test flow control (window exhaustion and replenishment)
// - Test GOAWAY handling
```

### Integration Tests

- [ ] Request to `https://www.google.com` over HTTP/2 (Google supports h2)
- [ ] Request to `https://httpbin.org` verifying automatic protocol selection
- [ ] Multiple concurrent requests to same host verifying multiplexing
- [ ] Large response body verifying flow control

## Next Steps

Once Phase 3B is complete and validated:

1. Move to [Phase 4: Pipeline Infrastructure](phase-04-pipeline.md)
2. Implement middleware pipeline
3. Create basic middlewares (Logging, Timeout, DefaultHeaders, Retry, Auth, Metrics)

## Notes

- HTTP/2 is a binary protocol on top of the same TCP+TLS connection from Phase 3
- No new socket management — reuses `TcpConnectionPool` and `TlsStreamWrapper`
- HPACK is the most implementation-intensive part — consider using existing C# HPACK libraries if available and IL2CPP-compatible
- Server push (PUSH_PROMISE) is not implemented in v1.0 — reject with RST_STREAM
- h2c (HTTP/2 over cleartext via Upgrade) is not implemented — only h2 over TLS
- **Critical risk:** ALPN support in `SslStream` must be validated on IL2CPP builds (iOS, Android) before committing to this phase
