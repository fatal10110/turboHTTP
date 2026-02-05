using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Manages a single HTTP/2 connection: initialization, request sending with HPACK headers + DATA,
    /// background frame reading/dispatch, flow control, stream multiplexing, and shutdown.
    /// RFC 7540 Sections 3.5, 4, 5, 6, 8.
    /// </summary>
    internal class Http2Connection : IDisposable
    {
        // Connection identity
        public string Host { get; }
        public int Port { get; }

        // I/O
        private readonly Stream _stream;
        private readonly Http2FrameCodec _codec;

        // HPACK (separate encoder/decoder, each with own dynamic table)
        private readonly HpackEncoder _hpackEncoder;
        private readonly HpackDecoder _hpackDecoder;

        // Stream management
        private readonly ConcurrentDictionary<int, Http2Stream> _activeStreams
            = new ConcurrentDictionary<int, Http2Stream>();
        private int _nextStreamId = 1;

        // Settings
        private readonly Http2Settings _localSettings = new Http2Settings();
        private readonly Http2Settings _remoteSettings = new Http2Settings();

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

        // CONTINUATION tracking
        private int _continuationStreamId;

        // Initialization handshake
        private TaskCompletionSource<bool> _settingsAckTcs =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // HPACK table size tracking (for detecting changes including back-to-default)
        private int _lastHeaderTableSize = Http2Constants.DefaultHeaderTableSize;

        public bool IsAlive =>
            !_goawayReceived &&
            !_cts.IsCancellationRequested &&
            _readLoopTask != null &&
            !_readLoopTask.IsCompleted;

        public Http2Connection(Stream stream, string host, int port)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            Host = host ?? throw new ArgumentNullException(nameof(host));
            Port = port;
            _codec = new Http2FrameCodec(stream);
            _hpackEncoder = new HpackEncoder();
            _hpackDecoder = new HpackDecoder();
        }

        /// <summary>
        /// Initialize the HTTP/2 connection: send preface + SETTINGS, start read loop,
        /// wait for server SETTINGS ACK.
        /// </summary>
        public async Task InitializeAsync(CancellationToken ct)
        {
            // 1. Write connection preface
            await _codec.WritePrefaceAsync(ct);

            // 2. Send initial SETTINGS frame
            byte[] settingsPayload = _localSettings.SerializeClientSettings();
            await _codec.WriteFrameAsync(new Http2Frame
            {
                Type = Http2FrameType.Settings,
                Flags = Http2FrameFlags.None,
                StreamId = 0,
                Payload = settingsPayload,
                Length = settingsPayload.Length
            }, ct);

            // 3. Start background read loop
            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));

            // 4. Wait for SETTINGS ACK with timeout
            var ackTask = _settingsAckTcs.Task;
            var timeoutTask = Task.Delay(5000, ct);
            var completed = await Task.WhenAny(ackTask, timeoutTask);

            if (completed == timeoutTask)
            {
                ct.ThrowIfCancellationRequested();
                throw new UHttpException(new UHttpError(UHttpErrorType.Timeout,
                    "HTTP/2 SETTINGS ACK timeout"));
            }

            await ackTask; // propagate any exception
        }

        /// <summary>
        /// Send an HTTP/2 request and wait for the response.
        /// </summary>
        public async Task<UHttpResponse> SendRequestAsync(
            UHttpRequest request, RequestContext context, CancellationToken ct)
        {
            // Step 1: Pre-flight checks
            if (_goawayReceived)
                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Connection received GOAWAY"));
            if (_cts.IsCancellationRequested)
                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Connection is closed"));

            // Step 2: Enforce SETTINGS_MAX_CONCURRENT_STREAMS (RFC 7540 Section 5.1.2)
            // NOTE: This check is not atomic with stream creation below. Under high
            // concurrency, multiple threads may pass this check simultaneously, briefly
            // exceeding the limit. The server handles this gracefully with REFUSED_STREAM.
            // A proper fix (SemaphoreSlim gated on MaxConcurrentStreams) is deferred to Phase 10.
            if (_activeStreams.Count >= _remoteSettings.MaxConcurrentStreams)
                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    $"MAX_CONCURRENT_STREAMS limit reached ({_remoteSettings.MaxConcurrentStreams})"));

            // Step 3: Allocate stream ID
            var streamId = Interlocked.Add(ref _nextStreamId, 2) - 2;
            if (streamId < 0 || streamId > int.MaxValue)
                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Stream ID space exhausted, close and reopen connection"));

            // Step 3: Create stream object (with both send and recv windows)
            var stream = new Http2Stream(streamId, request, context,
                _remoteSettings.InitialWindowSize, _localSettings.InitialWindowSize);
            _activeStreams[streamId] = stream;

            // Re-check shutdown after adding to _activeStreams
            if (_goawayReceived || _cts.IsCancellationRequested)
            {
                _activeStreams.TryRemove(streamId, out _);
                stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Connection closed during stream creation")));
                stream.Dispose();
                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Connection is closed"));
            }

            // Check if already cancelled
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
                stream.Dispose();
            });

            // Step 4: Build pseudo-headers + regular headers
            var headerList = new List<(string, string)>();
            headerList.Add((":method", request.Method.ToUpperString()));
            headerList.Add((":scheme", request.Uri.Scheme.ToLowerInvariant()));
            headerList.Add((":authority", BuildAuthorityValue(request.Uri)));
            headerList.Add((":path", request.Uri.PathAndQuery ?? "/"));

            foreach (var name in request.Headers.Names)
            {
                if (IsHttp2ForbiddenHeader(name)) continue;

                // RFC 7540 Section 8.1.2.2: te header is forbidden in HTTP/2
                // except with the value "trailers".
                if (string.Equals(name, "te", StringComparison.OrdinalIgnoreCase))
                {
                    var teValues = request.Headers.GetValues(name);
                    foreach (var v in teValues)
                    {
                        if (string.Equals(v.Trim(), "trailers", StringComparison.OrdinalIgnoreCase))
                            headerList.Add(("te", "trailers"));
                    }
                    continue;
                }

                foreach (var value in request.Headers.GetValues(name))
                    headerList.Add((name.ToLowerInvariant(), value));
            }

            if (!request.Headers.Contains("user-agent"))
                headerList.Add(("user-agent", "TurboHTTP/1.0"));

            // Step 5: HPACK encode headers + send HEADERS (under write lock)
            await _writeLock.WaitAsync(ct);
            try
            {
                byte[] headerBlock = _hpackEncoder.Encode(headerList);
                bool hasBody = request.Body != null && request.Body.Length > 0;

                await SendHeadersAsync(streamId, headerBlock, endStream: !hasBody, ct);

                stream.State = hasBody ? Http2StreamState.Open : Http2StreamState.HalfClosedLocal;
            }
            finally
            {
                _writeLock.Release();
            }

            // Step 7: Send DATA frames OUTSIDE write lock
            bool hasBody2 = request.Body != null && request.Body.Length > 0;
            if (hasBody2)
            {
                await SendDataAsync(streamId, request.Body, stream, ct);
                stream.State = Http2StreamState.HalfClosedLocal;
            }

            context.RecordEvent("TransportH2RequestSent");

            // Step 8: Wait for response
            return await stream.ResponseTcs.Task;
        }

        private async Task SendHeadersAsync(int streamId, byte[] headerBlock, bool endStream,
            CancellationToken ct)
        {
            int maxPayload = _remoteSettings.MaxFrameSize;

            if (headerBlock.Length <= maxPayload)
            {
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
                var firstPayload = new byte[maxPayload];
                Array.Copy(headerBlock, 0, firstPayload, 0, maxPayload);
                int offset = maxPayload;

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

        private async Task SendDataAsync(int streamId, byte[] body, Http2Stream stream,
            CancellationToken ct)
        {
            int offset = 0;
            while (offset < body.Length)
            {
                // M6: Stop sending if the stream was reset by the peer
                if (stream.ResponseTcs.Task.IsCompleted)
                    break;

                int available = Math.Min(
                    Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0),
                    stream.SendWindowSize);
                available = Math.Min(available, _remoteSettings.MaxFrameSize);
                available = Math.Min(available, body.Length - offset);

                if (available <= 0)
                {
                    await WaitForWindowUpdateAsync(ct);
                    continue;
                }

                bool isLast = (offset + available) >= body.Length;
                var payload = new byte[available];
                Array.Copy(body, offset, payload, 0, available);

                await _writeLock.WaitAsync(ct);
                bool lockHeld = true;
                try
                {
                    int connWindow = Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0);
                    int streamWindow = stream.SendWindowSize;
                    int actualAvailable = Math.Min(connWindow, streamWindow);
                    actualAvailable = Math.Min(actualAvailable, _remoteSettings.MaxFrameSize);
                    actualAvailable = Math.Min(actualAvailable, body.Length - offset);

                    if (actualAvailable <= 0)
                    {
                        _writeLock.Release();
                        lockHeld = false;
                        await WaitForWindowUpdateAsync(ct);
                        continue;
                    }

                    if (actualAvailable != available)
                    {
                        payload = new byte[actualAvailable];
                        Array.Copy(body, offset, payload, 0, actualAvailable);
                        available = actualAvailable;
                        isLast = (offset + available) >= body.Length;
                    }

                    Interlocked.Add(ref _connectionSendWindow, -available);
                    stream.AdjustSendWindowSize(-available);

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

        // --- Read Loop ---

        private async Task ReadLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Fix 3: Use local max frame size for inbound validation, not remote.
                    // _localSettings.MaxFrameSize is what WE are willing to receive.
                    // _remoteSettings.MaxFrameSize is what the PEER is willing to receive.
                    var frame = await _codec.ReadFrameAsync(_localSettings.MaxFrameSize, ct);

                    if (_continuationStreamId != 0 && frame.Type != Http2FrameType.Continuation)
                    {
                        throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                            $"Expected CONTINUATION for stream {_continuationStreamId}, got {frame.Type}");
                    }

                    switch (frame.Type)
                    {
                        case Http2FrameType.Data:         await HandleDataFrameAsync(frame, ct); break;
                        case Http2FrameType.Headers:      HandleHeadersFrame(frame); break;
                        case Http2FrameType.Continuation: HandleContinuationFrame(frame); break;
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
            catch (HpackDecodingException hdex)
            {
                // RFC 7540 Section 4.3: HPACK decoding error is a connection error
                // of type COMPRESSION_ERROR. Send GOAWAY before failing streams.
                var pex = new Http2ProtocolException(Http2ErrorCode.CompressionError,
                    hdex.Message);
                try
                {
                    await SendGoAwayAsync(Http2ErrorCode.CompressionError);
                }
                catch { /* best effort */ }
                FailAllStreams(pex);
            }
            catch (Http2ProtocolException pex)
            {
                // Send GOAWAY with the appropriate error code before failing streams
                try
                {
                    await SendGoAwayAsync(pex.ErrorCode);
                }
                catch { /* best effort */ }
                FailAllStreams(pex);
            }
            catch (Exception ex)
            {
                FailAllStreams(ex);
            }
        }

        // --- Frame Handlers ---

        private async Task HandleDataFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "DATA frame on stream 0");

            // Fix 8: Per RFC 7540 Section 6.1, the entire DATA frame payload including
            // padding is included in flow control. Use frame.Length, not stripped payload length.
            int flowControlledLength = frame.Length;

            // Handle padding — strip for body consumption but use frame.Length for window accounting
            byte[] dataPayload;
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                if (frame.Length < 1)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "DATA frame too short for padding");
                int padLength = frame.Payload[0];
                if (padLength >= frame.Length)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "DATA padding length exceeds frame");
                int dataLength = frame.Length - 1 - padLength;
                dataPayload = new byte[dataLength];
                Array.Copy(frame.Payload, 1, dataPayload, 0, dataLength);
            }
            else
            {
                dataPayload = frame.Payload;
            }

            // Validate connection-level flow control (uses full frame length including padding)
            if (flowControlledLength > _connectionRecvWindow)
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                    "DATA exceeds connection receive window");

            if (!_activeStreams.TryGetValue(frame.StreamId, out var stream))
            {
                // Still decrement connection recv window for unknown streams
                _connectionRecvWindow -= flowControlledLength;
                await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.StreamClosed);
                return;
            }

            // RFC 7540 Section 8.1: Response MUST start with HEADERS.
            // DATA before HEADERS is a protocol error — send RST_STREAM.
            if (!stream.HeadersReceived)
            {
                _connectionRecvWindow -= flowControlledLength;
                // Remove from active streams BEFORE Fail() to prevent race with Dispose().
                // Fail() triggers user continuations which could call Dispose() concurrently.
                if (_activeStreams.TryRemove(frame.StreamId, out _))
                {
                    stream.Fail(new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        $"DATA received before HEADERS on stream {frame.StreamId}"));
                    await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.ProtocolError);
                    stream.Dispose();
                }
                return;
            }

            // Validate stream-level flow control (Fix 1)
            if (flowControlledLength > stream.RecvWindowSize)
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                    $"DATA exceeds stream {frame.StreamId} receive window");

            // Enforce MaxResponseBodySize limit to prevent unbounded MemoryStream growth
            long maxBodySize = _localSettings.MaxResponseBodySize;
            if (maxBodySize > 0 && stream.ResponseBody.Length + dataPayload.Length > maxBodySize)
            {
                _connectionRecvWindow -= flowControlledLength;
                if (_activeStreams.TryRemove(frame.StreamId, out _))
                {
                    stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                        $"Response body exceeds maximum size limit ({maxBodySize} bytes)")));
                    await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.Cancel);
                    stream.Dispose();
                }
                return;
            }

            // Write to response body (only the actual data, not padding)
            stream.ResponseBody.Write(dataPayload, 0, dataPayload.Length);

            // Decrement recv windows (both connection and stream, using full frame length)
            _connectionRecvWindow -= flowControlledLength;
            stream.RecvWindowSize -= flowControlledLength;

            // Send connection-level WINDOW_UPDATE if needed
            if (_connectionRecvWindow < Http2Constants.DefaultInitialWindowSize / 2)
            {
                int increment = Http2Constants.DefaultInitialWindowSize - _connectionRecvWindow;
                await SendWindowUpdateAsync(0, increment, ct);
                _connectionRecvWindow = Http2Constants.DefaultInitialWindowSize;
            }

            // Send stream-level WINDOW_UPDATE if needed (Fix 1)
            if (stream.RecvWindowSize < _localSettings.InitialWindowSize / 2)
            {
                int increment = _localSettings.InitialWindowSize - stream.RecvWindowSize;
                await SendWindowUpdateAsync(frame.StreamId, increment, ct);
                stream.RecvWindowSize = _localSettings.InitialWindowSize;
            }

            // END_STREAM handling
            if (frame.HasFlag(Http2FrameFlags.EndStream))
            {
                if (stream.HeadersReceived)
                {
                    stream.Complete();
                    _activeStreams.TryRemove(frame.StreamId, out _);
                    stream.Dispose();
                }
            }
        }

        private void HandleHeadersFrame(Http2Frame frame)
        {
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "HEADERS frame on stream 0");

            if (!_activeStreams.TryGetValue(frame.StreamId, out var stream))
                return; // Stream already closed/cancelled

            int payloadStart = 0;
            int payloadLength = frame.Length;

            // Handle padding
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                if (frame.Length < 1)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "HEADERS frame too short for padding");
                int padLength = frame.Payload[0];
                payloadStart = 1;
                payloadLength -= (1 + padLength);
                if (payloadLength < 0)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "HEADERS padding length exceeds frame");
            }

            // Handle priority
            if (frame.HasFlag(Http2FrameFlags.HasPriority))
            {
                if (payloadLength < 5)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "HEADERS frame too short for priority");
                payloadStart += 5;
                payloadLength -= 5;
            }

            // Accumulate header block
            stream.AppendHeaderBlock(frame.Payload, payloadStart, payloadLength);

            // Track END_STREAM for deferred completion (when headers span CONTINUATION)
            if (frame.HasFlag(Http2FrameFlags.EndStream))
                stream.PendingEndStream = true;

            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            {
                DecodeAndSetHeaders(stream);

                if (stream.PendingEndStream && stream.HeadersReceived)
                {
                    stream.Complete();
                    _activeStreams.TryRemove(frame.StreamId, out _);
                    stream.Dispose();
                }
            }
            else
            {
                _continuationStreamId = frame.StreamId;
            }
        }

        private void HandleContinuationFrame(Http2Frame frame)
        {
            if (_continuationStreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "Unexpected CONTINUATION frame");

            if (_continuationStreamId != frame.StreamId)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    $"CONTINUATION for wrong stream (expected {_continuationStreamId}, got {frame.StreamId})");

            if (!_activeStreams.TryGetValue(frame.StreamId, out var stream))
                return;

            stream.AppendHeaderBlock(frame.Payload, 0, frame.Payload.Length);

            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            {
                _continuationStreamId = 0;
                DecodeAndSetHeaders(stream);

                if (stream.PendingEndStream && stream.HeadersReceived)
                {
                    stream.Complete();
                    _activeStreams.TryRemove(frame.StreamId, out _);
                    stream.Dispose();
                }
            }
        }

        private void DecodeAndSetHeaders(Http2Stream stream)
        {
            var headerBlock = stream.GetHeaderBlock();
            var decoded = _hpackDecoder.Decode(headerBlock, 0, headerBlock.Length);

            // Enforce SETTINGS_MAX_HEADER_LIST_SIZE (RFC 7540 Section 6.5.2).
            // Header list size = sum of (name length + value length + 32) for each header.
            int headerListSize = 0;
            foreach (var (n, v) in decoded)
            {
                headerListSize += n.Length + v.Length + 32;
            }
            if (headerListSize > _localSettings.MaxHeaderListSize)
            {
                stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    $"Response header list size {headerListSize} exceeds limit {_localSettings.MaxHeaderListSize}")));
                _activeStreams.TryRemove(stream.StreamId, out _);
                stream.Dispose();
                return;
            }

            var responseHeaders = new HttpHeaders();
            bool hasStatus = false;
            foreach (var (name, value) in decoded)
            {
                if (name == ":status")
                {
                    if (int.TryParse(value, out int statusCode) && statusCode >= 100 && statusCode <= 999)
                    {
                        stream.StatusCode = statusCode;
                        hasStatus = true;
                    }
                    else
                    {
                        stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            $"Invalid :status value: {value}")));
                        _activeStreams.TryRemove(stream.StreamId, out _);
                        stream.Dispose();
                        return;
                    }
                }
                else if (!name.StartsWith(":"))
                {
                    responseHeaders.Add(name, value);
                }
            }

            if (!hasStatus)
            {
                stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                    "Missing :status pseudo-header in HTTP/2 response")));
                _activeStreams.TryRemove(stream.StreamId, out _);
                stream.Dispose();
                return;
            }

            stream.ResponseHeaders = responseHeaders;
            stream.HeadersReceived = true;
            stream.ClearHeaderBlock();
        }

        private async Task HandleSettingsFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            // Fix 7: Check stream ID BEFORE checking ACK flag.
            // Per RFC 7540 Section 6.5, SETTINGS MUST be on stream 0 regardless of ACK.
            if (frame.StreamId != 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "SETTINGS on non-zero stream");

            if (frame.HasFlag(Http2FrameFlags.Ack))
            {
                if (frame.Length != 0)
                    throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                        "SETTINGS ACK with non-zero payload");
                _settingsAckTcs.TrySetResult(true);
                return;
            }

            var settings = Http2Settings.ParsePayload(frame.Payload);
            int oldInitialWindowSize = _remoteSettings.InitialWindowSize;

            foreach (var (id, value) in settings)
            {
                _remoteSettings.Apply(id, value);
            }

            // Adjust stream send windows if InitialWindowSize changed
            int newInitialWindowSize = _remoteSettings.InitialWindowSize;
            if (newInitialWindowSize != oldInitialWindowSize)
            {
                int delta = newInitialWindowSize - oldInitialWindowSize;
                foreach (var kvp in _activeStreams)
                {
                    var stream = kvp.Value;
                    long newWindow = (long)stream.SendWindowSize + delta;
                    if (newWindow > int.MaxValue || newWindow < 0)
                        throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                            "INITIAL_WINDOW_SIZE change causes stream window overflow");
                    stream.AdjustSendWindowSize(delta);
                }
            }

            // Fix 2: Update HPACK encoder whenever header table size changes from its
            // previous value, including changes back to the default.
            int newHeaderTableSize = _remoteSettings.HeaderTableSize;
            if (newHeaderTableSize != _lastHeaderTableSize)
            {
                _hpackEncoder.SetMaxDynamicTableSize(newHeaderTableSize);
                _lastHeaderTableSize = newHeaderTableSize;
            }

            // Send SETTINGS ACK
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Settings,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = Array.Empty<byte>(),
                    Length = 0
                }, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task HandlePingFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            if (frame.StreamId != 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PING on non-zero stream");

            if (frame.Payload.Length != 8)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "PING payload must be 8 bytes");

            if (frame.HasFlag(Http2FrameFlags.Ack))
                return; // We don't send PINGs proactively in Phase 3

            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.Ping,
                    Flags = Http2FrameFlags.Ack,
                    StreamId = 0,
                    Payload = frame.Payload,
                    Length = 8
                }, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private void HandleGoAwayFrame(Http2Frame frame)
        {
            if (frame.StreamId != 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "GOAWAY on non-zero stream");

            if (frame.Payload.Length < 8) return;

            int lastStreamId = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                               (frame.Payload[2] << 8) | frame.Payload[3];
            uint errorCode = ((uint)frame.Payload[4] << 24) | ((uint)frame.Payload[5] << 16) |
                             ((uint)frame.Payload[6] << 8) | frame.Payload[7];

            _goawayReceived = true;
            _lastGoawayStreamId = lastStreamId;

            // Fail streams above lastStreamId
            foreach (var kvp in _activeStreams)
            {
                if (kvp.Key > lastStreamId)
                {
                    if (_activeStreams.TryRemove(kvp.Key, out var stream))
                    {
                        stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            $"Server sent GOAWAY (error={errorCode}), stream {kvp.Key} was not processed")));
                        stream.Dispose();
                    }
                }
            }
        }

        private void HandleWindowUpdateFrame(Http2Frame frame)
        {
            if (frame.Payload.Length != 4)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "WINDOW_UPDATE payload must be 4 bytes");

            int increment = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                            (frame.Payload[2] << 8) | frame.Payload[3];

            if (increment == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "WINDOW_UPDATE increment must be non-zero");

            if (frame.StreamId == 0)
            {
                long newWindow = (long)Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0) + increment;
                if (newWindow > int.MaxValue)
                    throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                        "Connection send window overflow");
                Interlocked.Add(ref _connectionSendWindow, increment);
                try { _windowWaiter.Release(); } catch (SemaphoreFullException) { }
            }
            else
            {
                if (_activeStreams.TryGetValue(frame.StreamId, out var stream))
                {
                    long newWindow = (long)stream.SendWindowSize + increment;
                    if (newWindow > int.MaxValue)
                        throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                            $"Stream {frame.StreamId} send window overflow");
                    stream.AdjustSendWindowSize(increment);
                    try { _windowWaiter.Release(); } catch (SemaphoreFullException) { }
                }
            }
        }

        private void HandleRstStreamFrame(Http2Frame frame)
        {
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "RST_STREAM on stream 0");

            if (frame.Payload.Length != 4)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "RST_STREAM payload must be 4 bytes");

            uint errorCode = ((uint)frame.Payload[0] << 24) | ((uint)frame.Payload[1] << 16) |
                             ((uint)frame.Payload[2] << 8) | frame.Payload[3];

            if (_activeStreams.TryRemove(frame.StreamId, out var stream))
            {
                if ((Http2ErrorCode)errorCode == Http2ErrorCode.Cancel)
                    stream.Cancel();
                else
                    stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                        $"RST_STREAM: {(Http2ErrorCode)errorCode}")));
                stream.Dispose();
            }
        }

        private async Task RejectPushPromiseAsync(Http2Frame frame, CancellationToken ct)
        {
            if (frame.Payload.Length < 4) return;

            int promisedStreamId = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                                   (frame.Payload[2] << 8) | frame.Payload[3];

            await SendRstStreamAsync(promisedStreamId, Http2ErrorCode.RefusedStream);
        }

        // --- Helpers ---

        private async Task SendWindowUpdateAsync(int streamId, int increment, CancellationToken ct)
        {
            var payload = new byte[4];
            payload[0] = (byte)((increment >> 24) & 0x7F);
            payload[1] = (byte)((increment >> 16) & 0xFF);
            payload[2] = (byte)((increment >> 8) & 0xFF);
            payload[3] = (byte)(increment & 0xFF);

            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                await _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.WindowUpdate,
                    Flags = Http2FrameFlags.None,
                    StreamId = streamId,
                    Payload = payload,
                    Length = 4
                }, CancellationToken.None);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private async Task SendGoAwayAsync(Http2ErrorCode errorCode)
        {
            try
            {
                int lastStreamId = Math.Max(_nextStreamId - 2, 0);
                var payload = new byte[8];
                payload[0] = (byte)((lastStreamId >> 24) & 0x7F);
                payload[1] = (byte)((lastStreamId >> 16) & 0xFF);
                payload[2] = (byte)((lastStreamId >> 8) & 0xFF);
                payload[3] = (byte)(lastStreamId & 0xFF);
                payload[4] = (byte)(((uint)errorCode >> 24) & 0xFF);
                payload[5] = (byte)(((uint)errorCode >> 16) & 0xFF);
                payload[6] = (byte)(((uint)errorCode >> 8) & 0xFF);
                payload[7] = (byte)((uint)errorCode & 0xFF);

                await _writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.GoAway,
                        Flags = Http2FrameFlags.None,
                        StreamId = 0,
                        Payload = payload,
                        Length = 8
                    }, CancellationToken.None);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (ObjectDisposedException) { /* Connection being disposed, ignore */ }
        }

        private async Task SendRstStreamAsync(int streamId, Http2ErrorCode errorCode)
        {
            try
            {
                var payload = new byte[4];
                payload[0] = (byte)(((uint)errorCode >> 24) & 0xFF);
                payload[1] = (byte)(((uint)errorCode >> 16) & 0xFF);
                payload[2] = (byte)(((uint)errorCode >> 8) & 0xFF);
                payload[3] = (byte)((uint)errorCode & 0xFF);

                await _writeLock.WaitAsync(CancellationToken.None);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.RstStream,
                        Flags = Http2FrameFlags.None,
                        StreamId = streamId,
                        Payload = payload,
                        Length = 4
                    }, CancellationToken.None);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (ObjectDisposedException) { /* Connection being disposed, ignore */ }
        }

        private async Task WaitForWindowUpdateAsync(CancellationToken ct)
        {
            await _windowWaiter.WaitAsync(ct);
        }

        private void FailAllStreams(Exception ex)
        {
            _goawayReceived = true;
            _cts.Cancel();

            foreach (var kvp in _activeStreams)
            {
                if (_activeStreams.TryRemove(kvp.Key, out var stream))
                {
                    stream.Fail(ex);
                    stream.Dispose();
                }
            }
        }

        private static string BuildAuthorityValue(Uri uri)
        {
            var host = uri.Host;
            if (uri.HostNameType == UriHostNameType.IPv6)
                host = $"[{host}]";

            bool isDefaultPort = (uri.Scheme == "https" && uri.Port == 443)
                              || (uri.Scheme == "http" && uri.Port == 80);
            return isDefaultPort ? host : $"{host}:{uri.Port}";
        }

        private static bool IsHttp2ForbiddenHeader(string name)
        {
            return string.Equals(name, "connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "transfer-encoding", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "keep-alive", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "proxy-connection", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "upgrade", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "host", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _cts.Cancel();

            // Best-effort GOAWAY
            try
            {
                int lastStreamId = Math.Max(_nextStreamId - 2, 0);
                var goawayPayload = new byte[8];
                goawayPayload[0] = (byte)((lastStreamId >> 24) & 0x7F);
                goawayPayload[1] = (byte)((lastStreamId >> 16) & 0xFF);
                goawayPayload[2] = (byte)((lastStreamId >> 8) & 0xFF);
                goawayPayload[3] = (byte)(lastStreamId & 0xFF);
                // Error code = NO_ERROR (0x0) — bytes 4-7 are already 0

                _codec.WriteFrameAsync(new Http2Frame
                {
                    Type = Http2FrameType.GoAway,
                    Flags = Http2FrameFlags.None,
                    StreamId = 0,
                    Payload = goawayPayload,
                    Length = 8
                }, CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
            }
            catch { /* best effort */ }

            try { _readLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

            FailAllStreams(new ObjectDisposedException(nameof(Http2Connection)));

            _writeLock?.Dispose();
            _cts?.Dispose();
            _windowWaiter?.Dispose();
            _stream?.Dispose();
        }
    }
}
