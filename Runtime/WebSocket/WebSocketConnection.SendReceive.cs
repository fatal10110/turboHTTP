using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    public sealed partial class WebSocketConnection
    {
        public async Task SendTextAsync(string message, CancellationToken ct)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            int byteCount;
            try
            {
                byteCount = WebSocketConstants.StrictUtf8.GetByteCount(message);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Message is not valid UTF-8 encodable text.", nameof(message), ex);
            }

            if (byteCount == 0)
            {
                await SendMessageAsync(WebSocketOpcode.Text, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                return;
            }

            byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = WebSocketConstants.StrictUtf8.GetBytes(
                    message,
                    0,
                    message.Length,
                    payloadBuffer,
                    0);

                await SendMessageAsync(
                    WebSocketOpcode.Text,
                    new ReadOnlyMemory<byte>(payloadBuffer, 0, written),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payloadBuffer);
            }
        }

        public async Task SendTextAsync(ReadOnlyMemory<byte> utf8Payload, CancellationToken ct)
        {
            await SendMessageAsync(WebSocketOpcode.Text, utf8Payload, ct).ConfigureAwait(false);
        }

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await SendMessageAsync(WebSocketOpcode.Binary, payload, ct).ConfigureAwait(false);
        }

        public async ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            var state = State;
            if (state != WebSocketState.Open && state != WebSocketState.Closing)
            {
                throw new InvalidOperationException(
                    "ReceiveAsync requires Open or Closing state. Current state: " + state + ".");
            }

            try
            {
                return await _receiveQueue.DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (AsyncQueueCompletedException ex)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket connection is closed.",
                    ex,
                    _hasCloseStatus ? _closeStatus.Code : (WebSocketCloseCode?)null,
                    _hasCloseStatus ? _closeStatus.Reason : null);
            }
        }

        public async Task CloseAsync(WebSocketCloseCode code, string reason, CancellationToken ct)
        {
            ThrowIfDisposed();

            if (!WebSocketConstants.ValidateCloseCode((int)code, allowReservedLocal: false))
            {
                throw new WebSocketException(
                    WebSocketError.InvalidCloseCode,
                    "Close code is not valid for wire transmission.");
            }

            var state = State;
            if (state == WebSocketState.None || state == WebSocketState.Connecting)
            {
                throw new InvalidOperationException(
                    "CloseAsync requires Open, Closing, or Closed state. Current state: " + state + ".");
            }

            if (state == WebSocketState.Closed)
                return;

            if (state == WebSocketState.Open)
            {
                _ = TryTransitionState(WebSocketState.Open, WebSocketState.Closing);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.CloseHandshakeTimeout);
            var closeToken = timeoutCts.Token;

            bool timedOut = false;
            try
            {
                await SendCloseFrameIfNeededAsync(code, reason, closeToken).ConfigureAwait(false);
                await AwaitWithCancellation(_remoteCloseTcs.Task, closeToken).ConfigureAwait(false);
                if (_receiveLoopTask != null)
                    await AwaitWithCancellation(_receiveLoopTask, closeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }

            if (timedOut)
            {
                Abort();
                return;
            }

            var closeStatus = _hasCloseStatus
                ? _closeStatus
                : new WebSocketCloseStatus(code, reason ?? string.Empty);

            FinalizeClose(closeStatus, null);
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var frameLease = await _frameReader.ReadAsync(
                        _stream,
                        _messageAssembler.FragmentedMessageInProgress,
                        ct).ConfigureAwait(false);

                    if (frameLease == null)
                    {
                        if (State == WebSocketState.Closing)
                            break;

                        throw new IOException("Unexpected end of stream while reading WebSocket frame.");
                    }

                    _metrics?.RecordFrameReceived(frameLease.FrameByteCount);

                    if (frameLease.Frame.IsControlFrame)
                    {
                        bool shouldClose = await HandleControlFrameAsync(frameLease, ct).ConfigureAwait(false);

                        if (shouldClose)
                            break;

                        TouchActivity(applicationMessage: false);
                        continue;
                    }

                    if (!_messageAssembler.TryAssemble(frameLease, out var assembledMessage))
                        continue;

                    if (assembledMessage == null)
                        continue;

                    try
                    {
                        if (IsCompressionRsvBitSet(assembledMessage.RsvBits))
                            _metrics?.RecordCompressedInboundBytes(assembledMessage.PayloadLength);

                        using var transformedInbound = ApplyInboundExtensions(assembledMessage);
                        string decodedText = null;
                        var buffer = AcquireMessagePayloadBuffer(
                            assembledMessage,
                            transformedInbound,
                            out int payloadLength);

                        if (assembledMessage.Opcode == WebSocketOpcode.Text)
                        {
                            try
                            {
                                decodedText = payloadLength == 0
                                    ? string.Empty
                                    : WebSocketConstants.StrictUtf8.GetString(buffer, 0, payloadLength);
                            }
                            catch (Exception ex)
                            {
                                if (buffer != null)
                                    ArrayPool<byte>.Shared.Return(buffer);

                                throw new WebSocketProtocolException(
                                    WebSocketError.InvalidUtf8,
                                    "Received text frame payload is not valid UTF-8.",
                                    WebSocketCloseCode.InvalidPayload,
                                    ex);
                            }
                        }

                        var messageType = assembledMessage.Opcode == WebSocketOpcode.Text
                            ? WebSocketMessageType.Text
                            : WebSocketMessageType.Binary;

                        var wsMessage = new WebSocketMessage(messageType, buffer, payloadLength, decodedText);

                        try
                        {
                            await _receiveQueue.EnqueueAsync(wsMessage, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            wsMessage.Dispose();
                            throw;
                        }

                        _metrics?.RecordMessageReceived();
                        TryPublishMetricsUpdate();
                        TouchActivity(applicationMessage: true);
                    }
                    finally
                    {
                        assembledMessage.Dispose();
                    }
                }

                var finalStatus = _hasCloseStatus
                    ? _closeStatus
                    : new WebSocketCloseStatus(WebSocketCloseCode.NormalClosure);
                FinalizeClose(finalStatus, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during close/dispose.
            }
            catch (WebSocketProtocolException ex)
            {
                await TryHandleProtocolErrorAsync(ex, ct).ConfigureAwait(false);
            }
            catch (WebSocketException ex) when (
                ex.Error == WebSocketError.DecompressionFailed ||
                ex.Error == WebSocketError.DecompressedMessageTooLarge)
            {
                var protocolEx = new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    ex.Message,
                    WebSocketCloseCode.ProtocolError,
                    ex);

                await TryHandleProtocolErrorAsync(protocolEx, ct).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    var wsEx = new WebSocketException(
                        WebSocketError.ReceiveFailed,
                        "Socket error while receiving WebSocket frames.",
                        ex);

                    FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
                }
            }
            catch (IOException ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    var wsEx = new WebSocketException(
                        WebSocketError.ReceiveFailed,
                        "I/O error while receiving WebSocket frames.",
                        ex);

                    FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
                }
            }
            catch (Exception ex)
            {
                var wsEx = ex as WebSocketException ??
                    new WebSocketException(WebSocketError.ReceiveFailed, ex.Message, ex);

                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
            }
        }

        private async Task<bool> HandleControlFrameAsync(WebSocketFrameReadLease frameLease, CancellationToken ct)
        {
            try
            {
                var frame = frameLease.Frame;

                if (frame.Opcode == WebSocketOpcode.Ping)
                {
                    await SendLockedAsync(
                        allowClosingState: true,
                        applicationMessage: false,
                        (stream, token) => _frameWriter.WritePongAsync(stream, frame.Payload, token),
                        ct).ConfigureAwait(false);

                    _metrics?.RecordFrameSent(CalculateFrameWireLength(frame.Payload.Length, masked: true));
                    TryPublishMetricsUpdate();
                    return false;
                }

                if (frame.Opcode == WebSocketOpcode.Pong)
                {
                    long pongTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    Interlocked.Exchange(ref _lastPongTimestamp, pongTimestamp);
                    if (frame.Payload.Length == 8)
                    {
                        ulong pongCounter = BinaryPrimitives.ReadUInt64BigEndian(frame.Payload.Span);
                        Interlocked.Exchange(ref _lastPongCounter, unchecked((long)pongCounter));
                    }

                    Interlocked.Exchange(ref _pongWaiter, null)?.TrySetResult(pongTimestamp);
                    _metrics?.RecordPongReceived();

                    long lastPingTimestamp = Volatile.Read(ref _lastPingTimestamp);
                    if (_healthMonitor != null &&
                        lastPingTimestamp > 0 &&
                        pongTimestamp >= lastPingTimestamp)
                    {
                        var rtt = ElapsedBetweenStopwatch(lastPingTimestamp, pongTimestamp);
                        _healthMonitor.RecordRttSample(rtt, Metrics);
                    }

                    TryPublishMetricsUpdate();

                    return false;
                }

                if (frame.Opcode == WebSocketOpcode.Close)
                {
                    var remoteStatus = ParseCloseStatus(frame.Payload);
                    _remoteCloseTcs.TrySetResult(remoteStatus);
                    _hasCloseStatus = true;
                    _closeStatus = remoteStatus;

                    if (State == WebSocketState.Open)
                        _ = TryTransitionState(WebSocketState.Open, WebSocketState.Closing);

                    await SendCloseFrameIfNeededAsync(
                        WebSocketCloseCode.NormalClosure,
                        string.Empty,
                        ct).ConfigureAwait(false);

                    return true;
                }

                throw new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    "Unsupported control frame opcode.");
            }
            finally
            {
                frameLease.Dispose();
            }
        }

        private WebSocketCloseStatus ParseCloseStatus(ReadOnlyMemory<byte> payload)
        {
            if (payload.Length == 0)
                return new WebSocketCloseStatus(WebSocketCloseCode.NoStatusReceived);

            if (payload.Length == 1)
            {
                throw new WebSocketProtocolException(
                    WebSocketError.InvalidFrame,
                    "Close frame payload of 1 byte is invalid.",
                    WebSocketCloseCode.ProtocolError);
            }

            ushort code = BinaryPrimitives.ReadUInt16BigEndian(payload.Span.Slice(0, 2));
            if (!WebSocketConstants.ValidateCloseCode(code, allowReservedLocal: false))
            {
                throw new WebSocketProtocolException(
                    WebSocketError.InvalidCloseCode,
                    "Received invalid close status code.",
                    WebSocketCloseCode.ProtocolError);
            }

            string reason = string.Empty;
            if (payload.Length > 2)
            {
                try
                {
                    reason = WebSocketConstants.StrictUtf8.GetString(payload.Slice(2).Span);
                }
                catch (Exception ex)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.InvalidUtf8,
                        "Close frame reason is not valid UTF-8.",
                        WebSocketCloseCode.InvalidPayload,
                        ex);
                }
            }

            return new WebSocketCloseStatus((WebSocketCloseCode)code, reason);
        }

        private async Task TryHandleProtocolErrorAsync(WebSocketProtocolException ex, CancellationToken ct)
        {
            var error = new WebSocketException(
                WebSocketError.ProtocolViolation,
                ex.Message,
                ex,
                ex.CloseCode);

            try
            {
                if (State == WebSocketState.Open)
                    _ = TryTransitionState(WebSocketState.Open, WebSocketState.Closing);

                await SendCloseFrameIfNeededAsync(ex.CloseCode, string.Empty, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best effort close frame send on protocol violation.
            }

            FinalizeClose(new WebSocketCloseStatus(ex.CloseCode), error);
        }

        private async Task SendCloseFrameIfNeededAsync(WebSocketCloseCode code, string reason, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _closeFrameSent, 1, 0) != 0)
                return;

            await SendLockedAsync(
                allowClosingState: true,
                applicationMessage: false,
                (stream, token) => _frameWriter.WriteCloseAsync(stream, code, reason, token),
                ct).ConfigureAwait(false);

            int closePayloadLength = GetClosePayloadLength(reason);
            _metrics?.RecordFrameSent(CalculateFrameWireLength(closePayloadLength, masked: true));
            TryPublishMetricsUpdate();
        }

        private async Task SendMessageAsync(WebSocketOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await SendLockedAsync(
                allowClosingState: false,
                applicationMessage: true,
                (stream, token) => WriteMessageWithExtensionsAsync(stream, opcode, payload, token),
                ct).ConfigureAwait(false);
        }

        private async Task SendLockedAsync(
            bool allowClosingState,
            bool applicationMessage,
            Func<Stream, CancellationToken, Task> send,
            CancellationToken ct)
        {
            ThrowIfDisposed();

            var state = State;
            if (state == WebSocketState.Closed || state == WebSocketState.None || state == WebSocketState.Connecting)
            {
                throw new InvalidOperationException("Cannot send in state " + state + ".");
            }

            if (!allowClosingState && state != WebSocketState.Open)
            {
                throw new InvalidOperationException("Data frames can only be sent while connection is Open.");
            }

            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                state = State;
                if (state == WebSocketState.Closed)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosed, "Connection is closed.");
                }

                if (!allowClosingState && state != WebSocketState.Open)
                {
                    throw new InvalidOperationException("Data frames can only be sent while connection is Open.");
                }

                var stream = _stream;
                if (stream == null)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket stream is unavailable.");
                }

                await send(stream, ct).ConfigureAwait(false);
                TouchActivity(applicationMessage);
            }
            catch (IOException ex)
            {
                var wsEx = new WebSocketException(WebSocketError.SendFailed, "I/O error while sending frame.", ex);
                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
                throw wsEx;
            }
            catch (SocketException ex)
            {
                var wsEx = new WebSocketException(WebSocketError.SendFailed, "Socket error while sending frame.", ex);
                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
                throw wsEx;
            }
            catch (WebSocketException ex) when (
                ex.Error == WebSocketError.CompressionFailed ||
                ex.Error == WebSocketError.ProtocolViolation)
            {
                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), ex);
                throw;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task RunKeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var dueIn = _options.PingInterval - ElapsedSinceStopwatch(
                        Volatile.Read(ref _lastActivityStopwatchTimestamp));
                    if (dueIn > TimeSpan.Zero)
                        await Task.Delay(dueIn, ct).ConfigureAwait(false);

                    if (State != WebSocketState.Open)
                        break;

                    if (_options.IdleTimeout > TimeSpan.Zero)
                    {
                        var idleFor = ElapsedSinceStopwatch(
                            Volatile.Read(ref _lastApplicationMessageStopwatchTimestamp));
                        if (idleFor > _options.IdleTimeout)
                        {
                            var timeoutEx = new WebSocketException(
                                WebSocketError.ConnectionClosed,
                                "WebSocket idle timeout elapsed.");

                            FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), timeoutEx);
                            break;
                        }
                    }

                    long pingCounter = Interlocked.Increment(ref _pingCounter);
                    long pingTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

                    byte[] pingPayload = ArrayPool<byte>.Shared.Rent(8);
                    try
                    {
                        BinaryPrimitives.WriteUInt64BigEndian(
                            new Span<byte>(pingPayload, 0, 8),
                            unchecked((ulong)pingCounter));

                        try
                        {
                            await SendLockedAsync(
                                allowClosingState: false,
                                applicationMessage: false,
                                (stream, token) => _frameWriter.WritePingAsync(
                                    stream,
                                    new ReadOnlyMemory<byte>(pingPayload, 0, 8),
                                    token),
                                ct).ConfigureAwait(false);

                            _metrics?.RecordFrameSent(CalculateFrameWireLength(8, masked: true));
                            _metrics?.RecordPingSent();
                            TryPublishMetricsUpdate();
                        }
                        catch (InvalidOperationException) when (State != WebSocketState.Open)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pingPayload);
                    }

                    Interlocked.Exchange(ref _lastPingTimestamp, pingTimestamp);

                    bool gotPong = await WaitForPongAfterAsync(pingTimestamp, _options.PongTimeout, ct)
                        .ConfigureAwait(false);
                    if (!gotPong)
                    {
                        var timeoutEx = new WebSocketException(
                            WebSocketError.PongTimeout,
                            "Pong timeout elapsed.");

                        FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), timeoutEx);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during shutdown.
            }
            catch (Exception ex)
            {
                var wsEx = ex as WebSocketException ??
                    new WebSocketException(WebSocketError.PongTimeout, ex.Message, ex);

                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
            }
        }

        private async Task<bool> WaitForPongAfterAsync(long pingTimestamp, TimeSpan timeout, CancellationToken ct)
        {
            if (timeout <= TimeSpan.Zero)
                return true;

            if (Volatile.Read(ref _lastPongTimestamp) >= pingTimestamp)
                return true;

            var waiter = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _pongWaiter, waiter);

            if (Volatile.Read(ref _lastPongTimestamp) >= pingTimestamp)
            {
                Interlocked.CompareExchange(ref _pongWaiter, null, waiter);
                return true;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await AwaitWithCancellation(waiter.Task, timeoutCts.Token).ConfigureAwait(false);
                return Volatile.Read(ref _lastPongTimestamp) >= pingTimestamp;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Volatile.Read(ref _lastPongTimestamp) >= pingTimestamp;
            }
            finally
            {
                Interlocked.CompareExchange(ref _pongWaiter, null, waiter);
            }
        }
    }
}
