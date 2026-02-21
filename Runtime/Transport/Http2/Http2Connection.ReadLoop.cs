using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal partial class Http2Connection
    {
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
                            case Http2FrameType.Data:         await HandleDataFrameAsync(frame, ct); break;
                            case Http2FrameType.Headers:      HandleHeadersFrame(frame); break;
                            case Http2FrameType.Continuation: HandleContinuationFrame(frame); break;
                            case Http2FrameType.Settings:     await HandleSettingsFrameAsync(frame, ct); break;
                            case Http2FrameType.Ping:         await HandlePingFrameAsync(frame, ct); break;
                            case Http2FrameType.GoAway:       HandleGoAwayFrame(frame); break;
                            case Http2FrameType.WindowUpdate: await HandleWindowUpdateFrameAsync(frame); break;
                            case Http2FrameType.RstStream:    HandleRstStreamFrame(frame); break;
                            case Http2FrameType.PushPromise:  await RejectPushPromiseAsync(frame); break;
                            case Http2FrameType.Priority:     HandlePriorityFrame(frame); break;
                        }
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

            // Enforce MaxResponseBodySize limit to prevent unbounded response buffering.
            long maxBodySize = _localSettings.MaxResponseBodySize;
            if (maxBodySize > 0 && stream.ResponseBodyLength + dataLength > maxBodySize)
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

            // Write to response body (only the actual data, not padding).
            // A canceled stream can be disposed concurrently after being removed from
            // _activeStreams; ignore write-after-dispose and retire the stream.
            bool responseBodyDisposed = false;
            if (dataLength > 0)
            {
                try
                {
                    stream.AppendResponseData(frame.Payload, dataOffset, dataLength);
                }
                catch (ObjectDisposedException)
                {
                    responseBodyDisposed = true;
                }
            }

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

            if (responseBodyDisposed)
            {
                _activeStreams.TryRemove(frame.StreamId, out _);
                return;
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
                DecodeAndSetHeaders(stream, isTrailingHeaders: stream.HeadersReceived);

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

            stream.AppendHeaderBlock(frame.Payload, 0, frame.Length);

            if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            {
                _continuationStreamId = 0;
                DecodeAndSetHeaders(stream, isTrailingHeaders: stream.HeadersReceived);

                if (stream.PendingEndStream && stream.HeadersReceived)
                {
                    stream.Complete();
                    _activeStreams.TryRemove(frame.StreamId, out _);
                    stream.Dispose();
                }
            }
        }

        private void DecodeAndSetHeaders(Http2Stream stream, bool isTrailingHeaders)
        {
            var headerBlock = stream.GetHeaderBlockSegment();
            var headerBytes = headerBlock.Array ?? Array.Empty<byte>();
            var decoded = _hpackDecoder.Decode(headerBytes, headerBlock.Offset, headerBlock.Count);

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

            var responseHeaders = isTrailingHeaders
                ? stream.ResponseHeaders ?? new HttpHeaders()
                : new HttpHeaders();
            bool hasStatus = isTrailingHeaders;
            foreach (var (name, value) in decoded)
            {
                if (name == ":status")
                {
                    if (isTrailingHeaders)
                    {
                        stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                            "Unexpected :status pseudo-header in trailing headers")));
                        _activeStreams.TryRemove(stream.StreamId, out _);
                        stream.Dispose();
                        return;
                    }

                    if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int statusCode)
                        && statusCode >= 100 && statusCode <= 999)
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
                else if (isTrailingHeaders)
                {
                    stream.Fail(new UHttpException(new UHttpError(UHttpErrorType.NetworkError,
                        $"Unexpected pseudo-header in trailing headers: {name}")));
                    _activeStreams.TryRemove(stream.StreamId, out _);
                    stream.Dispose();
                    return;
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

            if (TryGetContentLength(responseHeaders, _localSettings.MaxResponseBodySize, out int contentLength))
                stream.EnsureResponseBodyCapacity(contentLength);

            stream.ResponseHeaders = responseHeaders;
            stream.HeadersReceived = true;
            stream.ClearHeaderBlock();
        }

        private static bool TryGetContentLength(
            HttpHeaders headers, long maxResponseBodySize, out int contentLength)
        {
            contentLength = 0;
            var value = headers.Get("content-length");
            if (string.IsNullOrEmpty(value))
                return false;

            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
                return false;
            if (parsed <= 0)
                return false;

            if (maxResponseBodySize <= 0)
                return false;

            long cap = Math.Min(maxResponseBodySize, int.MaxValue);
            if (cap <= 0)
                return false;

            contentLength = (int)Math.Min(parsed, cap);
            return true;
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

            var settings = Http2Settings.ParsePayload(frame.Payload, frame.Length);
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
                return; // We don't send PINGs proactively in Phase 3

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

                if (_activeStreams.TryRemove(frame.StreamId, out var erroredStream))
                {
                    erroredStream.Fail(new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                        $"Invalid WINDOW_UPDATE increment=0 on stream {frame.StreamId}"));
                    erroredStream.Dispose();
                }

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

        private async Task RejectPushPromiseAsync(Http2Frame frame)
        {
            // RFC 9113 Section 8.4:
            // A client that sent SETTINGS_ENABLE_PUSH = 0 MUST treat PUSH_PROMISE as
            // a connection error of type PROTOCOL_ERROR.
            if (!_localSettings.EnablePush)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                    "Received PUSH_PROMISE after advertising ENABLE_PUSH=0");

            if (frame.Length < 4) return;

            int promisedStreamId = ((frame.Payload[0] & 0x7F) << 24) | (frame.Payload[1] << 16) |
                                   (frame.Payload[2] << 8) | frame.Payload[3];

            await SendRstStreamAsync(promisedStreamId, Http2ErrorCode.RefusedStream);
        }
    }
}
