using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
        // Read-loop owned: a stream reset decided on the synchronous header-block path. ReadLoopAsync
        // writes it right after the frame is handled, so the reset keeps frame order and the header
        // path keeps its synchronous shape (one field block concerns exactly one stream).
        private int _pendingResetStreamId;
        private Http2ErrorCode _pendingResetCode;

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
                    using (var frameLease = await _codec.ReadFrameLeaseAsync(_localSettings.MaxFrameSize, ct))
                    {
                        var frame = frameLease.Frame;

                        if (_continuationStreamId != 0 && frame.Type != Http2FrameType.Continuation)
                        {
                            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                                $"Expected CONTINUATION for stream {_continuationStreamId}, got {frame.Type}");
                        }

                        switch (frame.Type)
                        {
                            case Http2FrameType.Data: await HandleDataFrameAsync(frame, ct); break;
                            case Http2FrameType.Headers: HandleHeadersFrame(frame); break;
                            case Http2FrameType.Continuation: HandleContinuationFrame(frame); break;
                            case Http2FrameType.Settings: await HandleSettingsFrameAsync(frame, ct); break;
                            case Http2FrameType.Ping: await HandlePingFrameAsync(frame, ct); break;
                            case Http2FrameType.GoAway: HandleGoAwayFrame(frame); break;
                            case Http2FrameType.WindowUpdate: await HandleWindowUpdateFrameAsync(frame); break;
                            case Http2FrameType.RstStream: HandleRstStreamFrame(frame); break;
                            case Http2FrameType.PushPromise: await RejectPushPromiseAsync(frame); break;
                            case Http2FrameType.Priority: HandlePriorityFrame(frame); break;
                        }

                        if (_pendingResetStreamId != 0)
                            await FlushPendingStreamResetAsync().ConfigureAwait(false);
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
            finally
            {
                ReleaseHeaderBlockBuffer();
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
            int dataOffset = 0;
            int dataLength = flowControlledLength;
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                if (frame.Length < 1)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "DATA frame too short for padding");
                int padLength = frame.Payload[0];
                if (padLength >= frame.Length)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "DATA padding length exceeds frame");
                dataOffset = 1;
                dataLength = frame.Length - 1 - padLength;
            }

            // Validate connection-level flow control (uses full frame length including padding)
            int connectionRecvWindow = Interlocked.CompareExchange(ref _connectionRecvWindow, 0, 0);
            if (flowControlledLength > connectionRecvWindow)
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                    "DATA exceeds connection receive window");

            if (!TryAcquireActiveStream(frame.StreamId, out var stream))
            {
                // Post-reset DATA still counts against the connection window.
                Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);

                if (!IsRecentlyResetStream(frame.StreamId))
                    await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.StreamClosed).ConfigureAwait(false);
                return;
            }

            // The lease keeps the stream from being returned to the pool (and re-rented by another
            // request) while this frame mutates it.
            try
            {
                // RFC 9113 Section 5.1: DATA after the peer's END_STREAM (half-closed remote while the request
                // body is still being sent) is a stream error of type STREAM_CLOSED.
                if (stream.IsRemoteClosed)
                {
                    Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                    await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);
                    if (RemoveActiveStream(frame.StreamId, stream))
                    {
                        stream.Fail(new Http2ProtocolException(Http2ErrorCode.StreamClosed,
                            $"DATA received after END_STREAM on stream {frame.StreamId}"));
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.StreamClosed).ConfigureAwait(false);
                    }
                    return;
                }

                // RFC 9113 Section 8.1: a response MUST start with HEADERS.
                // DATA before HEADERS is a protocol error — send RST_STREAM.
                if (!stream.HeadersReceived)
                {
                    Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                    await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);
                    if (RemoveActiveStream(frame.StreamId, stream))
                    {
                        stream.Fail(new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                            $"DATA received before HEADERS on stream {frame.StreamId}"));
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.ProtocolError).ConfigureAwait(false);
                    }
                    return;
                }

                // Validate stream-level flow control (Fix 1)
                if (flowControlledLength > stream.RecvWindowSize)
                    throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                        $"DATA exceeds stream {frame.StreamId} receive window");

                // Opt-in hard cap for every HTTP/2 response (Http2Options.MaxResponseBodySize, 0 = none).
                // Buffered responses are capped where they accumulate (UHttpClientOptions.MaxBufferedResponseBodySize).
                long maxBodySize = _localSettings.MaxResponseBodySize;
                if (maxBodySize > 0 && stream.ResponseBodyLength + dataLength > maxBodySize)
                {
                    Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                    stream.AdjustRecvWindowSize(-flowControlledLength);
                    await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);
                    if (RemoveActiveStream(frame.StreamId, stream))
                    {
                        stream.FaultResponseBody(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            $"Response body exceeds maximum size limit ({maxBodySize} bytes)")));
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
                    }
                    return;
                }

                // RFC 9113 Section 8.1.1: more DATA than the content-length is a malformed response.
                long expectedBodyLength = stream.ExpectedResponseBodyLength;
                if (expectedBodyLength >= 0 && stream.ResponseBodyLength + dataLength > expectedBodyLength)
                {
                    Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                    stream.AdjustRecvWindowSize(-flowControlledLength);
                    await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);
                    await ResetMalformedResponseAsync(stream,
                        $"DATA exceeds content-length {expectedBodyLength}").ConfigureAwait(false);
                    return;
                }

                // Decrement recv windows (both connection and stream, using full frame length)
                Interlocked.Add(ref _connectionRecvWindow, -flowControlledLength);
                stream.AdjustRecvWindowSize(-flowControlledLength);

                Http2ResponseBodyEnqueueResult enqueueResult = Http2ResponseBodyEnqueueResult.Accepted;
                if (dataLength > 0)
                {
                    stream.AddResponseBodyBytes(dataLength);
                    enqueueResult = stream.TryAppendResponseData(
                        frame.Payload,
                        dataOffset,
                        dataLength,
                        flowControlledLength);
                }
                else if (flowControlledLength > 0 && !IsStreamClosedForReceive(frame.StreamId))
                {
                    // Credit the window before advertising it (RFC 9113 Section 6.9). A failed write fails
                    // the connection, so the credit is never observed by a live stream.
                    stream.AdjustRecvWindowSize(flowControlledLength);
                    await SendWindowUpdateAsync(frame.StreamId, flowControlledLength, ct).ConfigureAwait(false);
                }

                await MaybeSendConnectionWindowUpdateAsync(ct).ConfigureAwait(false);

                if (enqueueResult == Http2ResponseBodyEnqueueResult.BufferFull)
                {
                    if (RemoveActiveStream(frame.StreamId, stream))
                    {
                        stream.FaultResponseBody(new UHttpException(new UHttpError(
                            UHttpErrorType.NetworkError,
                            $"HTTP/2 response body exceeded the per-stream receive buffer ({PerStreamReceiveBufferBytes} bytes).")));
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.Cancel).ConfigureAwait(false);
                    }
                    return;
                }

                if (enqueueResult == Http2ResponseBodyEnqueueResult.Aborted)
                {
                    // The consumer abandoned the body. Whoever removes the stream first resets it: here, or
                    // the body source's cleanup (AbortStreamFromBodySourceAsync).
                    await ResetStreamIfActiveAsync(frame.StreamId, stream, Http2ErrorCode.Cancel).ConfigureAwait(false);
                    return;
                }

                // END_STREAM handling
                if (frame.HasFlag(Http2FrameFlags.EndStream))
                {
                    // RFC 9113 Section 8.1.1: fewer DATA bytes than the content-length is malformed.
                    if (expectedBodyLength >= 0 && stream.ResponseBodyLength != expectedBodyLength)
                    {
                        await ResetMalformedResponseAsync(stream,
                            $"DATA total {stream.ResponseBodyLength} does not match content-length {expectedBodyLength}")
                            .ConfigureAwait(false);
                        return;
                    }

                    // Leave the active map before the body completes: a reader that saw the end then never
                    // finds the stream still registered.
                    bool closedBoth = stream.MarkRemoteClosed(out bool resetNoError);
                    bool removed = closedBoth && RemoveActiveStream(frame.StreamId, stream);
                    stream.CompleteResponseBody();
                    if (removed && resetNoError)
                    {
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.NoError).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                stream.ReleaseLease();
            }
        }

        /// <summary>
        /// Looks up an active stream and takes a lease on it (see <see cref="Http2Stream.TryAcquireLease"/>).
        /// The caller releases it with <see cref="Http2Stream.ReleaseLease"/>.
        /// </summary>
        private bool TryAcquireActiveStream(int streamId, out Http2Stream stream)
        {
            if (_activeStreams.TryGetValue(streamId, out stream) && stream.TryAcquireLease(streamId, this))
                return true;

            stream = null;
            return false;
        }

        /// <summary>
        /// RFC 9113 Section 8.1.1: a malformed response is a stream error of type PROTOCOL_ERROR. Fails
        /// the request or faults the response body, and resets the stream if it is still active.
        /// </summary>
        private async Task ResetMalformedResponseAsync(Http2Stream stream, string reason)
        {
            int streamId = stream.StreamId;
            if (!RemoveActiveStream(streamId, stream))
                return;

            stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                "Malformed HTTP/2 response: " + reason)));
            TrackRecentlyResetStream(streamId);
            await SendRstStreamAsync(streamId, Http2ErrorCode.ProtocolError).ConfigureAwait(false);
        }

        /// <summary>
        /// Synchronous header-path counterpart of <see cref="ResetMalformedResponseAsync"/>: the stream is
        /// failed now (which stops its upload before the reset is written) and the RST_STREAM is queued
        /// for <see cref="FlushPendingStreamResetAsync"/>.
        /// </summary>
        private void RejectMalformedResponse(Http2Stream stream, string reason)
        {
            int streamId = stream.StreamId;
            bool removed = RemoveActiveStream(streamId, stream);
            stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                "Malformed HTTP/2 response: " + reason)));
            if (removed)
                QueueStreamReset(streamId, Http2ErrorCode.ProtocolError);
        }

        private void QueueStreamReset(int streamId, Http2ErrorCode errorCode)
        {
            TrackRecentlyResetStream(streamId);
            _pendingResetStreamId = streamId;
            _pendingResetCode = errorCode;
        }

        private async Task FlushPendingStreamResetAsync()
        {
            int streamId = _pendingResetStreamId;
            _pendingResetStreamId = 0;
            await SendRstStreamAsync(streamId, _pendingResetCode).ConfigureAwait(false);
        }

        private void HandleHeadersFrame(Http2Frame frame)
        {
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "HEADERS frame on stream 0");

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

            BeginHeaderBlock(frame, payloadStart, payloadLength,
                endStream: frame.HasFlag(Http2FrameFlags.EndStream), isPushPromise: false);
        }

        /// <summary>
        /// Starts a field block carried by HEADERS or PUSH_PROMISE. The block is accumulated and
        /// decoded whether or not its stream is still active (RFC 9113 Section 4.3): skipping it would
        /// desynchronize the HPACK dynamic table for every later stream on the connection.
        /// </summary>
        private void BeginHeaderBlock(Http2Frame frame, int offset, int length, bool endStream, bool isPushPromise)
        {
            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            {
                // Single-frame block: decode straight from the frame payload, no copy.
                CompleteHeaderBlock(frame.StreamId, frame.Payload, offset, length, endStream, isPushPromise);
                return;
            }

            if (length > HeaderBlockByteLimit)
                throw HeaderBlockTooLarge(length);

            EnsureHeaderBlockCapacity(length);
            if (length > 0)
                Buffer.BlockCopy(frame.Payload, offset, _headerBlockBuffer, 0, length);
            _headerBlockLength = length;
            _headerBlockContinuationCount = 0;
            _headerBlockEndStream = endStream;
            _headerBlockIsPushPromise = isPushPromise;
            _continuationStreamId = frame.StreamId;
        }

        private void HandleContinuationFrame(Http2Frame frame)
        {
            if (_continuationStreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "Unexpected CONTINUATION frame");

            if (_continuationStreamId != frame.StreamId)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    $"CONTINUATION for wrong stream (expected {_continuationStreamId}, got {frame.StreamId})");

            // RFC 9113 Section 10.5.1: bound the frame count (zero-length frames included) and the
            // accumulated bytes before buffering anything.
            if (++_headerBlockContinuationCount > MaxContinuationFramesPerHeaderBlock)
                throw new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm,
                    $"Header block exceeds {MaxContinuationFramesPerHeaderBlock} CONTINUATION frames");

            int newLength = _headerBlockLength + frame.Length;
            if (newLength > HeaderBlockByteLimit)
                throw HeaderBlockTooLarge(newLength);

            EnsureHeaderBlockCapacity(newLength);
            if (frame.Length > 0)
                Buffer.BlockCopy(frame.Payload, 0, _headerBlockBuffer, _headerBlockLength, frame.Length);
            _headerBlockLength = newLength;

            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            {
                int streamId = _continuationStreamId;
                _continuationStreamId = 0;
                try
                {
                    CompleteHeaderBlock(streamId, _headerBlockBuffer, 0, _headerBlockLength,
                        _headerBlockEndStream, _headerBlockIsPushPromise);
                }
                finally
                {
                    ReleaseHeaderBlockBuffer();
                }
            }
        }

        /// <summary>
        /// Decodes a complete field block (always, to keep the HPACK table in order, RFC 7541
        /// Section 2.2), then routes the field list to its stream if it is still active, or discards it.
        /// PUSH_PROMISE blocks are always discarded: the promised stream is refused.
        /// </summary>
        private void CompleteHeaderBlock(int streamId, byte[] block, int offset, int count, bool endStream, bool isPushPromise)
        {
            _hpackDecoder.Decode(block ?? Array.Empty<byte>(), offset, count, _decodedHeaderScratch);

            if (isPushPromise || !TryAcquireActiveStream(streamId, out var stream))
            {
                _decodedHeaderScratch.Clear();
                return;
            }

            try
            {
                // RFC 9113 Section 5.1: a field block after the peer's END_STREAM (half-closed remote while
                // the request body is still being sent) is a stream error of type STREAM_CLOSED. The block
                // is already decoded, so the HPACK table stays in order.
                if (stream.IsRemoteClosed)
                {
                    _decodedHeaderScratch.Clear();
                    if (RemoveActiveStream(streamId, stream))
                    {
                        stream.Fail(new Http2ProtocolException(Http2ErrorCode.StreamClosed,
                            $"HEADERS received after END_STREAM on stream {streamId}"));
                        QueueStreamReset(streamId, Http2ErrorCode.StreamClosed);
                    }

                    return;
                }

                ApplyDecodedHeaders(stream, isTrailingHeaders: stream.HeadersReceived, endStream: endStream);
            }
            finally
            {
                stream.ReleaseLease();
            }
        }

        // Encoded block cap: the advertised header list size (bounded by 256 KB), never below one
        // full frame so a single-frame block that is accepted is also accepted when split.
        private int HeaderBlockByteLimit =>
            Math.Max(_localSettings.MaxFrameSize, Math.Min(_localSettings.MaxHeaderListSize, MaxHeaderBlockBytesCeiling));

        private Http2ProtocolException HeaderBlockTooLarge(int length)
        {
            return new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm,
                $"Header block of {length} bytes exceeds limit {HeaderBlockByteLimit}");
        }

        private void EnsureHeaderBlockCapacity(int required)
        {
            var current = _headerBlockBuffer;
            if (current != null && current.Length >= required)
                return;

            int grown = Math.Min(HeaderBlockByteLimit, Math.Max(4096, (current?.Length ?? 0) * 2));
            var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, grown));
            if (current != null)
            {
                Buffer.BlockCopy(current, 0, next, 0, _headerBlockLength);
                ArrayPool<byte>.Shared.Return(current);
            }

            _headerBlockBuffer = next;
        }

        private void ReleaseHeaderBlockBuffer()
        {
            var buffer = _headerBlockBuffer;
            _headerBlockBuffer = null;
            _headerBlockLength = 0;
            _headerBlockContinuationCount = 0;
            _headerBlockEndStream = false;
            _headerBlockIsPushPromise = false;
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        private void ApplyDecodedHeaders(Http2Stream stream, bool isTrailingHeaders, bool endStream)
        {
            var decoded = _decodedHeaderScratch;
            try
            {

                // Enforce SETTINGS_MAX_HEADER_LIST_SIZE (RFC 7540 Section 6.5.2).
                // Header list size = sum of (name length + value length + 32) for each header.
                int headerListSize = 0;
                foreach (var (n, v) in decoded)
                {
                    headerListSize += n.Length + v.Length + 32;
                }
                if (headerListSize > _localSettings.MaxHeaderListSize)
                {
                    RejectMalformedResponse(stream, $"Response header list size {headerListSize} exceeds limit {_localSettings.MaxHeaderListSize}");
                    return;
                }

                var responseHeaders = new HttpHeaders();
                var statusCode = 0;
                var hasStatus = false;
                foreach (var (name, value) in decoded)
                {
                    if (name == ":status")
                    {
                        if (isTrailingHeaders)
                        {
                            RejectMalformedResponse(stream, "Unexpected :status pseudo-header in trailing headers");
                            return;
                        }

                        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out statusCode)
                            && statusCode >= 100 && statusCode <= 999)
                        {
                            hasStatus = true;
                        }
                        else
                        {
                            RejectMalformedResponse(stream, $"Invalid :status value: {value}");
                            return;
                        }
                    }
                    else if (!name.StartsWith(":", StringComparison.Ordinal))
                    {
                        // RFC 9113 Section 8.2.1 / 8.2.2: malformed fields are a stream error.
                        var malformed = GetMalformedResponseFieldReason(name, value);
                        if (malformed != null)
                        {
                            RejectMalformedResponse(stream, malformed);
                            return;
                        }

                        responseHeaders.Add(name, value);
                    }
                    else if (isTrailingHeaders)
                    {
                        RejectMalformedResponse(stream, $"Unexpected pseudo-header in trailing headers: {name}");
                        return;
                    }
                    else
                    {
                        RejectMalformedResponse(stream, $"Unexpected pseudo-header in response headers: {name}");
                        return;
                    }
                }

                if (!isTrailingHeaders && !hasStatus)
                {
                    RejectMalformedResponse(stream, "Missing :status pseudo-header in HTTP/2 response");
                    return;
                }

                if (isTrailingHeaders)
                {
                    if (!endStream)
                    {
                        RejectMalformedResponse(stream, "Trailing HEADERS frame received without END_STREAM flag (RFC 9113 §8.1)");
                        return;
                    }

                    // RFC 9113 Section 8.1.1: the DATA total must equal the content-length.
                    long expectedBodyLength = stream.ExpectedResponseBodyLength;
                    if (expectedBodyLength >= 0 && stream.ResponseBodyLength != expectedBodyLength)
                    {
                        RejectMalformedResponse(stream,
                            $"DATA total {stream.ResponseBodyLength} does not match content-length {expectedBodyLength}");
                        return;
                    }

                    stream.AppendTrailers(responseHeaders);
                    bool closedBoth = stream.MarkRemoteClosed(out bool resetNoError);
                    bool removed = closedBoth && RemoveActiveStream(stream.StreamId, stream);
                    stream.CompleteResponseBody();
                    if (removed && resetNoError)
                        QueueStreamReset(stream.StreamId, Http2ErrorCode.NoError);
                }
                else
                {
                    if (statusCode >= 100 && statusCode < 200)
                    {
                        // RFC 9113 Section 8.1: an interim response with END_STREAM is malformed.
                        if (endStream)
                        {
                            RejectMalformedResponse(stream,
                                $"interim {statusCode} response carries END_STREAM");
                            return;
                        }

                        if (statusCode == 100)
                            stream.SignalExpectContinueContinue();

                        return;
                    }

                    var contentLength = GetContentLength(responseHeaders);

                    // RFC 9113 Section 8.1.1: a non-zero content-length with no DATA is malformed, unless
                    // the response has no content by definition (RFC 9110 Section 6.4.1).
                    if (endStream && contentLength.HasValue && contentLength.Value > 0 &&
                        !Http2Stream.IsNoContentResponse(stream.Request.Method, statusCode))
                    {
                        RejectMalformedResponse(stream,
                            $"content-length {contentLength.Value} but the response ended without DATA");
                        return;
                    }

                    // A response that ends here closes the remote side before the handler sees it.
                    if (endStream)
                    {
                        bool closedBoth = stream.MarkRemoteClosed(out bool resetNoError);
                        if (closedBoth && RemoveActiveStream(stream.StreamId, stream) && resetNoError)
                            QueueStreamReset(stream.StreamId, Http2ErrorCode.NoError);
                    }

                    stream.SignalExpectContinueFinalResponse();
                    stream.TryStartResponse(
                        statusCode,
                        responseHeaders,
                        contentLength,
                        endStream);
                }
            }
            finally
            {
                decoded.Clear();
            }
        }

        /// <summary>
        /// RFC 9113 Section 8.2.1 and 8.2.2 checks for a decoded regular response field (headers or
        /// trailers). Returns null when the field is valid, else a reason that never echoes the value.
        /// </summary>
        internal static string GetMalformedResponseFieldReason(string name, string value)
        {
            if (string.IsNullOrEmpty(name))
                return "empty field name";

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c >= 'A' && c <= 'Z')
                    return "uppercase character in field name";
                if (c <= 0x20 || c >= 0x7F)
                    return "invalid character in field name";
            }

            if (IsConnectionSpecificField(name))
                return "connection-specific field '" + name + "'";

            if (value != null && value.Length > 0)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c == '\0' || c == '\r' || c == '\n')
                        return "invalid character in value of field '" + name + "'";
                }

                char first = value[0];
                char last = value[value.Length - 1];
                if (first == ' ' || first == '\t' || last == ' ' || last == '\t')
                    return "leading or trailing whitespace in value of field '" + name + "'";
            }

            return null;
        }

        private static bool IsConnectionSpecificField(string name)
        {
            switch (name)
            {
                case "connection":
                case "keep-alive":
                case "proxy-connection":
                case "transfer-encoding":
                case "upgrade":
                    return true;
                default:
                    return false;
            }
        }

        private static long? GetContentLength(HttpHeaders headers)
        {
            var value = headers.Get("content-length");
            if (string.IsNullOrEmpty(value))
                return null;

            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                return null;
            if (parsed < 0)
                return null;

            return parsed;
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
                try
                {
                    _settingsAckSource.SetResult(true);
                }
                catch (InvalidOperationException)
                {
                    // ACK source was already completed (timeout/cancellation race).
                }
                return;
            }

            var settings = Http2Settings.ParsePayload(frame.Payload, frame.Length);
            int initialWindowDelta = 0;

            // RFC 9113 Section 6.9.2: applying INITIAL_WINDOW_SIZE and adjusting every open stream by the
            // delta is one step with respect to stream creation. DispatchAsync snapshots the initial
            // window and registers the stream under this same monitor, so a new stream gets either the
            // old value plus the delta or the new value, never both or neither. No await in here.
            lock (_streamCreateLock)
            {
                int oldInitialWindowSize = _remoteSettings.InitialWindowSize;

                foreach (var (id, value) in settings)
                {
                    _remoteSettings.Apply(id, value);

                    // RFC 7541 Section 4.2: every HEADER_TABLE_SIZE value counts toward the smallest size
                    // the encoder must signal before its next block. The encoder only records the change;
                    // it resizes its table inside Encode, under _writeLock.
                    if (id == Http2SettingId.HeaderTableSize)
                        _hpackEncoder.SetMaxDynamicTableSize(_remoteSettings.HeaderTableSize);
                }

                // A raised MAX_CONCURRENT_STREAMS admits queued requests; a lowered one only affects new
                // streams (RFC 9113 Section 6.5.2: open streams above the limit are not closed).
                PumpAdmissionWaitersLocked();

                int newInitialWindowSize = _remoteSettings.InitialWindowSize;
                if (newInitialWindowSize != oldInitialWindowSize)
                {
                    initialWindowDelta = newInitialWindowSize - oldInitialWindowSize;
                    foreach (var kvp in _activeStreams)
                    {
                        var stream = kvp.Value;
                        if (!stream.TryAcquireLease(kvp.Key, this))
                            continue;

                        try
                        {
                            // A decrease may leave a window negative; senders wait until it is positive
                            // again. Only an overflow past 2^31-1 is a connection FLOW_CONTROL_ERROR.
                            long newWindow = (long)stream.SendWindowSize + initialWindowDelta;
                            if (newWindow > int.MaxValue)
                                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                                    "INITIAL_WINDOW_SIZE change causes stream window overflow");
                            stream.AdjustSendWindowSize(initialWindowDelta);
                        }
                        finally
                        {
                            stream.ReleaseLease();
                        }
                    }
                }
            }

            if (initialWindowDelta > 0)
                WakeSendWindowWaiters();

            // Send SETTINGS ACK
            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Settings,
                        Flags = Http2FrameFlags.Ack,
                        StreamId = 0,
                        Payload = Array.Empty<byte>(),
                        Length = 0
                    }, ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
            {
                // Connection is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
        }

        private async Task HandlePingFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            if (frame.StreamId != 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PING on non-zero stream");

            if (frame.Length != 8)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "PING payload must be 8 bytes");

            if (frame.HasFlag(Http2FrameFlags.Ack))
            {
                OnPingAck(frame.Payload);
                return;
            }

            try
            {
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _codec.WriteFrameAsync(new Http2Frame
                    {
                        Type = Http2FrameType.Ping,
                        Flags = Http2FrameFlags.Ack,
                        StreamId = 0,
                        Payload = frame.Payload,
                        Length = 8
                    }, ct).ConfigureAwait(false);
                }
                finally
                {
                    _writeLock.Release();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _cts.IsCancellationRequested)
            {
                // Connection is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // Connection is shutting down.
            }
        }

        private void HandleGoAwayFrame(Http2Frame frame)
        {
            if (frame.StreamId != 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "GOAWAY on non-zero stream");

            if (frame.Length < 8)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "GOAWAY payload must be at least 8 bytes");

            int lastStreamId = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                               (frame.Payload[2] << 8) | frame.Payload[3];
            uint errorCode = ((uint)frame.Payload[4] << 24) | ((uint)frame.Payload[5] << 16) |
                             ((uint)frame.Payload[6] << 8) | frame.Payload[7];

            // RFC 9113 Section 6.8: no new streams from now on; a later GOAWAY may lower the last stream id
            // but never raise it. Applied under _streamCreateLock, so every stream DispatchAsync registers
            // is either refused there or visible to the scan below. Queued requests are refused (not sent).
            lock (_streamCreateLock)
            {
                if (_goawayReceived)
                    lastStreamId = Math.Min(lastStreamId, _lastGoawayStreamId);

                _lastGoawayStreamId = lastStreamId;
                Volatile.Write(ref _goawayErrorCode, errorCode);
                _goawayReceived = true;
                PumpAdmissionWaitersLocked();
            }

            // Streams above lastStreamId were not processed (Section 8.7): the transport replays them on
            // another connection for any method. Streams at or below it keep running (drain).
            List<KeyValuePair<int, Http2Stream>> unprocessed = null;
            foreach (var kvp in _activeStreams)
            {
                if (kvp.Key > lastStreamId && _activeStreams.TryRemove(kvp.Key, out var stream))
                {
                    unprocessed ??= new List<KeyValuePair<int, Http2Stream>>();
                    unprocessed.Add(new KeyValuePair<int, Http2Stream>(kvp.Key, stream));
                }
            }

            // Nothing left to drain: close now, before the unprocessed streams' requests observe their
            // failure, so a replay never finds this connection still published as alive.
            TryCompleteDrain(inline: true);

            if (unprocessed == null)
                return;

            foreach (var kvp in unprocessed)
            {
                FailStreamUnderLease(kvp.Key, kvp.Value, new Http2RetryableRequestException(
                    new UHttpError(
                        UHttpErrorType.NetworkError,
                        $"Server sent GOAWAY (error={(Http2ErrorCode)errorCode}), stream {kvp.Key} was not processed"),
                    requestNotProcessed: true));
            }
        }

        private async Task HandleWindowUpdateFrameAsync(Http2Frame frame)
        {
            if (frame.Length != 4)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "WINDOW_UPDATE payload must be 4 bytes");

            int increment = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                            (frame.Payload[2] << 8) | frame.Payload[3];

            if (increment == 0)
            {
                // RFC 7540 Section 6.9: stream-level WINDOW_UPDATE with zero increment is
                // a stream error; connection-level (stream 0) is a connection error.
                if (frame.StreamId == 0)
                {
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "WINDOW_UPDATE increment must be non-zero");
                }

                if (TryRemoveActiveStream(frame.StreamId, out var erroredStream))
                {
                    FailStreamUnderLease(frame.StreamId, erroredStream, new Http2ProtocolException(
                        Http2ErrorCode.ProtocolError,
                        $"Invalid WINDOW_UPDATE increment=0 on stream {frame.StreamId}"));
                }

                TrackRecentlyResetStream(frame.StreamId);
                await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.ProtocolError).ConfigureAwait(false);
                return;
            }

            if (frame.StreamId == 0)
            {
                long newWindow = (long)Interlocked.CompareExchange(ref _connectionSendWindow, 0, 0) + increment;
                if (newWindow > int.MaxValue)
                    throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                        "Connection send window overflow");
                Interlocked.Add(ref _connectionSendWindow, increment);
                WakeSendWindowWaiters();
            }
            else
            {
                if (TryAcquireActiveStream(frame.StreamId, out var stream))
                {
                    bool overflow;
                    try
                    {
                        overflow = (long)stream.SendWindowSize + increment > int.MaxValue;
                        if (overflow)
                        {
                            // RFC 9113 Section 6.9.1: a stream window above 2^31-1 is a stream error of type
                            // FLOW_CONTROL_ERROR; only the connection window overflow is a connection error.
                            if (RemoveActiveStream(frame.StreamId, stream))
                            {
                                stream.Fail(new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                                    $"Stream {frame.StreamId} send window overflow"));
                            }
                        }
                        else
                        {
                            stream.AdjustSendWindowSize(increment);
                        }
                    }
                    finally
                    {
                        stream.ReleaseLease();
                    }

                    if (overflow)
                    {
                        TrackRecentlyResetStream(frame.StreamId);
                        await SendRstStreamAsync(frame.StreamId, Http2ErrorCode.FlowControlError)
                            .ConfigureAwait(false);
                        return;
                    }

                    WakeSendWindowWaiters();
                }
            }
        }

        private void HandlePriorityFrame(Http2Frame frame)
        {
            // RFC 7540 Section 6.3: PRIORITY frames are exactly 5 bytes and never stream 0.
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PRIORITY frame on stream 0");

            if (frame.Length != 5)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "PRIORITY frame payload must be 5 bytes");

            int dependencyStreamId = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                                     (frame.Payload[2] << 8) | frame.Payload[3];
            if (dependencyStreamId == frame.StreamId)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PRIORITY frame cannot depend on itself");
        }

        private void HandleRstStreamFrame(Http2Frame frame)
        {
            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "RST_STREAM on stream 0");

            if (frame.Length != 4)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "RST_STREAM payload must be 4 bytes");

            uint errorCode = ((uint)frame.Payload[0] << 24) | ((uint)frame.Payload[1] << 16) |
                             ((uint)frame.Payload[2] << 8) | frame.Payload[3];

            if (TryRemoveActiveStream(frame.StreamId, out var stream))
            {
                // After a complete response, RST_STREAM(NO_ERROR) only stops the upload: the response
                // is kept (RFC 9113 Section 8.1); Fail leaves a completed body untouched.
                var error = new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"RST_STREAM: {(Http2ErrorCode)errorCode}");

                // REFUSED_STREAM: the peer did no application processing (RFC 9113 Section 8.7), so the
                // transport may replay the request for any method. A stream error never ends the
                // connection (Section 5.4.2).
                FailStreamUnderLease(frame.StreamId, stream, errorCode == (uint)Http2ErrorCode.RefusedStream
                    ? new Http2RetryableRequestException(error, requestNotProcessed: true)
                    : new UHttpException(error));
            }
        }

        private async Task RejectPushPromiseAsync(Http2Frame frame)
        {
            // RFC 9113 Section 8.4:
            // A client that sent SETTINGS_ENABLE_PUSH = 0 MUST treat PUSH_PROMISE as
            // a connection error of type PROTOCOL_ERROR.
            if (!_localSettings.EnablePush)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "Received PUSH_PROMISE after advertising ENABLE_PUSH=0");

            if (frame.StreamId == 0)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PUSH_PROMISE frame on stream 0");

            int payloadStart = 0;
            int payloadLength = frame.Length;
            if (frame.HasFlag(Http2FrameFlags.Padded))
            {
                if (frame.Length < 1)
                    throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        "PUSH_PROMISE frame too short for padding");
                payloadStart = 1;
                payloadLength -= 1 + frame.Payload[0];
            }

            if (payloadLength < 4)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "PUSH_PROMISE frame too short for promised stream id");

            int promisedStreamId = ((frame.Payload[payloadStart] & 0x7F) << 24) |
                                   (frame.Payload[payloadStart + 1] << 16) |
                                   (frame.Payload[payloadStart + 2] << 8) |
                                   frame.Payload[payloadStart + 3];

            // RFC 9113 Section 4.3: the promised request's field block must be decoded (and its
            // CONTINUATION frames accepted) even though the push is refused, or the HPACK table desyncs.
            BeginHeaderBlock(frame, payloadStart + 4, payloadLength - 4, endStream: false, isPushPromise: true);

            await SendRstStreamAsync(promisedStreamId, Http2ErrorCode.RefusedStream);
        }
    }
}
