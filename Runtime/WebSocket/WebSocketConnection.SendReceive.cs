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

            // Closed still drains (#217): FinalizeClose completes the queue before the Closed transition,
            // so messages that arrived ahead of the peer's close frame are returned first and the
            // completed queue then reports ConnectionClosed.
            var state = State;
            var queue = _receiveQueue;
            if (state != WebSocketState.Open &&
                state != WebSocketState.Closing &&
                !(state == WebSocketState.Closed && queue != null))
            {
                throw new InvalidOperationException(
                    "ReceiveAsync requires Open, Closing, or Closed state. Current state: " + state + ".");
            }

            try
            {
                return await queue.DequeueAsync(ct).ConfigureAwait(false);
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
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                timedOut = true;
            }
            catch (OperationCanceledException)
            {
                // A canceled close must not leave the connection half-closed in Closing (#86).
                AbortCanceledClose();
                throw;
            }

            if (timedOut)
            {
                AbortCore(validateDisposed: false);
                return;
            }

            // The peer's Close arrived. The receive loop now waits, bounded by CloseHandshakeTimeout, for
            // the server to close TCP first (RFC 6455 section 7.1.1) and then finalizes.
            var receiveLoopTask = _receiveLoopTask;
            if (receiveLoopTask != null)
            {
                try
                {
                    await AwaitWithCancellation(receiveLoopTask, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    AbortCanceledClose();
                    throw;
                }
            }

            var closeStatus = _hasCloseStatus
                ? _closeStatus
                : new WebSocketCloseStatus(code, reason ?? string.Empty);

            FinalizeClose(closeStatus, null);
        }

        private void AbortCanceledClose()
        {
            FinalizeClose(
                new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket close was canceled; the connection was aborted."));
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            // The loop holds a resource reference so FinalizeClose on another thread cannot dispose the
            // extensions or reset the assembler while a frame is being processed (#84).
            if (!TryAcquireResources())
                return;

            try
            {
                await ReceiveLoopCoreAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ReleaseResources();
            }
        }

        private async Task ReceiveLoopCoreAsync(CancellationToken ct)
        {
            try
            {
                bool closeFrameReceived = false;
                while (!ct.IsCancellationRequested)
                {
                    var stream = Volatile.Read(ref _stream);
                    if (stream == null)
                        break;

                    var frameLease = await _frameReader.ReadAsync(
                        stream,
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
                        {
                            closeFrameReceived = true;
                            break;
                        }

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
                            if (!_receiveQueue.TryEnqueue(wsMessage))
                                await EnqueueWhileParkedAsync(wsMessage, ct).ConfigureAwait(false);
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

                if (closeFrameReceived)
                    await AwaitServerTransportCloseAsync(ct).ConfigureAwait(false);

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
                    // Takes only the frame lock, so it goes out between the fragments of a large
                    // in-flight message instead of waiting for all of it (RFC 6455 section 5.4, #86).
                    await WriteFrameLockedAsync(
                        OutboundFrame.Control(WebSocketOpcode.Pong, frame.Payload),
                        allowClosingState: true,
                        applicationMessage: false,
                        messageStarted: false,
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

                    // RFC 6455 section 5.5.1: the reply echoes the received status code; a Close without
                    // a body (1005 locally, never sent on the wire, 7.4.1) is answered with an empty one.
                    WebSocketCloseCode? replyCode = remoteStatus.Code == WebSocketCloseCode.NoStatusReceived
                        ? (WebSocketCloseCode?)null
                        : remoteStatus.Code;

                    using var replyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    if (_options.CloseHandshakeTimeout > TimeSpan.Zero)
                        replyCts.CancelAfter(_options.CloseHandshakeTimeout);

                    await SendCloseFrameIfNeededAsync(replyCode, string.Empty, replyCts.Token)
                        .ConfigureAwait(false);

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

        /// <summary>
        /// Waits, bounded by <c>CloseHandshakeTimeout</c>, for the server to close the TCP connection after
        /// the Close exchange (RFC 6455 section 7.1.1: the server closes first; the client may close after a
        /// timeout). Anything the server still sends is discarded.
        /// </summary>
        private async Task AwaitServerTransportCloseAsync(CancellationToken ct)
        {
            var stream = Volatile.Read(ref _stream);
            var timeout = _options.CloseHandshakeTimeout;
            if (stream == null || timeout <= TimeSpan.Zero)
                return;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            // Disposing the stream faults a read the platform does not cancel (#226), so the read below
            // always completes before its buffer is returned.
            using var closeOnTimeout = timeoutCts.Token.Register(
                static state => SafeDispose((Stream)state),
                stream);

            byte[] buffer = ArrayPool<byte>.Shared.Rent(256);
            try
            {
                while (await stream.ReadAsync(buffer, 0, buffer.Length, timeoutCts.Token).ConfigureAwait(false) > 0)
                {
                }
            }
            catch (Exception)
            {
                // Timeout, abort or reset: the close handshake itself already completed.
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private Task SendCloseFrameIfNeededAsync(WebSocketCloseCode? code, string reason, CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _closeFrameSent, 1, 0) != 0)
                return Task.CompletedTask;

            return SendCloseFrameAsync(code, reason, ct);
        }

        private async Task SendCloseFrameAsync(WebSocketCloseCode? code, string reason, CancellationToken ct)
        {
            // The Close frame waits for an in-flight data message instead of cutting it short: no data frame
            // may follow it (RFC 6455 section 5.5.1), and the current message may be finished first.
            await _messageLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WriteFrameLockedAsync(
                    OutboundFrame.Close(code, reason),
                    allowClosingState: true,
                    applicationMessage: false,
                    messageStarted: false,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _messageLock.Release();
            }

            int closePayloadLength = code.HasValue ? GetClosePayloadLength(reason) : 0;
            _metrics?.RecordFrameSent(CalculateFrameWireLength(closePayloadLength, masked: true));
            TryPublishMetricsUpdate();
        }

        private async Task SendMessageAsync(WebSocketOpcode opcode, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            ThrowIfCannotSend(allowClosingState: false);

            // The caller token may cancel the wait for the message lock and anything before the first byte.
            await _messageLock.WaitAsync(ct).ConfigureAwait(false);
            bool resourcesHeld = false;
            try
            {
                ThrowIfCannotSend(allowClosingState: false);

                if (!TryAcquireResources())
                    throw CreateClosedException();
                resourcesHeld = true;

                await WriteMessageWithExtensionsAsync(opcode, payload, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                (ex is WebSocketException failed &&
                 (failed.Error == WebSocketError.CompressionFailed ||
                  failed.Error == WebSocketError.ProtocolViolation)) ||
                (resourcesHeld &&
                 !(ex is WebSocketException) &&
                 !(ex is ArgumentException) &&
                 !(ex is OperationCanceledException)))
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one (#320).
                if (ex is WebSocketException extensionFailure)
                {
                    // The outbound extension state can no longer be trusted.
                    FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), extensionFailure);
                    throw;
                }

                var wsEx = new WebSocketException(
                    WebSocketError.SendFailed,
                    "Unexpected error while sending message; the connection was aborted.",
                    ex);
                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);
                throw wsEx;
            }
            finally
            {
                if (resourcesHeld)
                    ReleaseResources();

                _messageLock.Release();
            }
        }

        /// <summary>
        /// Writes a (possibly extension-transformed) message as one frame or as fragments, each through the
        /// frame lock, so control frames can be injected between fragments (RFC 6455 section 5.4, #86). The
        /// caller holds the message lock, so no other data frame can interleave.
        /// </summary>
        private async Task WriteMessageFramesAsync(
            WebSocketOpcode opcode,
            ReadOnlyMemory<byte> payload,
            byte rsvBits,
            CancellationToken ct)
        {
            // With an extension active the outbound state (permessage-deflate context takeover, RFC 7692
            // section 7.2.1) already includes this message, so it is committed before its first byte: a
            // cancellation from here aborts instead of leaving the peer's decompressor out of step.
            bool committed = _activeExtensions.Count > 0;

            int threshold = _options.FragmentationThreshold;
            if (payload.Length <= threshold)
            {
                await WriteFrameLockedAsync(
                    OutboundFrame.Data(opcode, isFinal: true, payload, rsvBits),
                    allowClosingState: true,
                    applicationMessage: true,
                    messageStarted: committed,
                    ct).ConfigureAwait(false);
                return;
            }

            int offset = 0;
            while (offset < payload.Length)
            {
                int fragmentLength = Math.Min(threshold, payload.Length - offset);
                bool first = offset == 0;
                bool isFinal = offset + fragmentLength >= payload.Length;

                await WriteFrameLockedAsync(
                    OutboundFrame.Data(
                        first ? opcode : WebSocketOpcode.Continuation,
                        isFinal,
                        payload.Slice(offset, fragmentLength),
                        first ? rsvBits : (byte)0),
                    allowClosingState: true,
                    applicationMessage: true,
                    messageStarted: committed || !first,
                    ct).ConfigureAwait(false);

                offset += fragmentLength;
            }
        }

        /// <summary>
        /// The single frame-write funnel: every frame goes through the frame lock and a resource reference,
        /// so FinalizeClose on another thread never disposes the writer under it (#84).
        /// </summary>
        private async Task WriteFrameLockedAsync(
            OutboundFrame frame,
            bool allowClosingState,
            bool applicationMessage,
            bool messageStarted,
            CancellationToken ct)
        {
            // The caller token may cancel the wait for the frame lock and anything before the first byte of a
            // message. Once bytes are on the wire a partial frame or message cannot be recovered (RFC 6455
            // 5.2, 5.4), so any failure from that point aborts the connection instead of leaving it Open (#79).
            bool lockHeld = false;
            bool resourcesHeld = false;
            bool writeStarted = messageStarted;
            try
            {
                if (applicationMessage)
                {
                    await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Increment(ref _controlFramesWaiting);
                    try
                    {
                        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _controlFramesWaiting);
                    }
                }

                lockHeld = true;

                ThrowIfCannotSend(allowClosingState);

                if (!TryAcquireResources())
                    throw CreateClosedException();
                resourcesHeld = true;

                var stream = Volatile.Read(ref _stream);
                var writer = Volatile.Read(ref _frameWriter);
                if (stream == null || writer == null)
                {
                    throw new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket stream is unavailable.");
                }

                ct.ThrowIfCancellationRequested();
                writeStarted = true;
                await frame.WriteAsync(writer, stream, ct).ConfigureAwait(false);
                TouchActivity(applicationMessage);
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is SocketException ||
                (writeStarted && !(ex is WebSocketException) && !(ex is ArgumentException)))
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one, which mapped a control-frame cancellation to SendFailed (#320).
                // ArgumentException is argument validation before any byte is written; a stream disposed
                // by a concurrent abort surfaces here as ObjectDisposedException and is mapped (#84).
                WebSocketException wsEx;
                if (ex is IOException)
                {
                    wsEx = new WebSocketException(WebSocketError.SendFailed, "I/O error while sending frame.", ex);
                }
                else if (ex is SocketException)
                {
                    wsEx = new WebSocketException(WebSocketError.SendFailed, "Socket error while sending frame.", ex);
                }
                else if (ex is OperationCanceledException)
                {
                    wsEx = new WebSocketException(
                        WebSocketError.SendFailed,
                        "Send was cancelled after the frame was started; the connection was aborted.",
                        ex);
                }
                else
                {
                    wsEx = new WebSocketException(
                        WebSocketError.SendFailed,
                        "Unexpected error while sending frame; the connection was aborted.",
                        ex);
                }

                FinalizeClose(new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure), wsEx);

                // Control-frame callers (close handshake timeout, keep-alive shutdown) handle cancellation.
                if (ex is OperationCanceledException && !applicationMessage)
                    throw;

                throw wsEx;
            }
            finally
            {
                if (resourcesHeld)
                    ReleaseResources();

                if (lockHeld)
                    _sendLock.Release();
            }
        }

        /// <summary>
        /// None/Connecting is a usage error (InvalidOperationException). A connection that is closing or
        /// closed fails sends with <see cref="WebSocketException"/>, whichever side of a concurrent abort,
        /// pong timeout or close the send lands on (#84).
        /// </summary>
        private void ThrowIfCannotSend(bool allowClosingState)
        {
            var state = State;
            if (state == WebSocketState.None || state == WebSocketState.Connecting)
                throw new InvalidOperationException("Cannot send in state " + state + ".");

            if (state == WebSocketState.Closed)
                throw CreateClosedException();

            if (!allowClosingState &&
                (state != WebSocketState.Open || Volatile.Read(ref _closeFrameSent) != 0))
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "Data frames can only be sent while the connection is Open.");
            }
        }

        private WebSocketException CreateClosedException()
        {
            return new WebSocketException(
                WebSocketError.ConnectionClosed,
                "WebSocket connection is closed.",
                null,
                _hasCloseStatus ? _closeStatus.Code : (WebSocketCloseCode?)null,
                _hasCloseStatus ? _closeStatus.Reason : null);
        }

        /// <summary>One frame for <see cref="WriteFrameLockedAsync"/>; no per-frame closure allocation.</summary>
        private readonly struct OutboundFrame
        {
            private OutboundFrame(
                WebSocketOpcode opcode,
                bool isFinal,
                ReadOnlyMemory<byte> payload,
                byte rsvBits,
                WebSocketCloseCode? closeCode,
                string closeReason)
            {
                Opcode = opcode;
                IsFinal = isFinal;
                Payload = payload;
                RsvBits = rsvBits;
                CloseCode = closeCode;
                CloseReason = closeReason;
            }

            public WebSocketOpcode Opcode { get; }
            public bool IsFinal { get; }
            public ReadOnlyMemory<byte> Payload { get; }
            public byte RsvBits { get; }
            public WebSocketCloseCode? CloseCode { get; }
            public string CloseReason { get; }

            public static OutboundFrame Data(WebSocketOpcode opcode, bool isFinal, ReadOnlyMemory<byte> payload, byte rsvBits)
            {
                return new OutboundFrame(opcode, isFinal, payload, rsvBits, null, null);
            }

            public static OutboundFrame Control(WebSocketOpcode opcode, ReadOnlyMemory<byte> payload)
            {
                return new OutboundFrame(opcode, isFinal: true, payload, 0, null, null);
            }

            /// <summary>A Close frame; a null code sends a Close without a body.</summary>
            public static OutboundFrame Close(WebSocketCloseCode? code, string reason)
            {
                return new OutboundFrame(WebSocketOpcode.Close, isFinal: true, ReadOnlyMemory<byte>.Empty, 0, code, reason);
            }

            public Task WriteAsync(WebSocketFrameWriter writer, Stream stream, CancellationToken ct)
            {
                if (Opcode == WebSocketOpcode.Close && CloseCode.HasValue)
                    return writer.WriteCloseAsync(stream, CloseCode.Value, CloseReason ?? string.Empty, ct);

                return writer.WriteFrameAsync(stream, Opcode, IsFinal, Payload, ct, RsvBits);
            }
        }

        /// <summary>
        /// Waits for space in the delivery queue when the application is not draining it. The socket is
        /// not read meanwhile (TCP backpressure), so the keep-alive loop must not treat the missing pong
        /// as a dead peer; it checks <c>_receiveParked</c> and <c>_receiveUnparkedTimestamp</c>.
        /// </summary>
        private async ValueTask EnqueueWhileParkedAsync(WebSocketMessage message, CancellationToken ct)
        {
            Interlocked.Exchange(ref _receiveParked, 1);
            try
            {
                await _receiveQueue.EnqueueAsync(message, ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _receiveUnparkedTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _receiveParked, 0);
            }
        }

        private bool ReceiveLoopParkedSince(long timestamp)
        {
            return Volatile.Read(ref _receiveParked) != 0 ||
                Volatile.Read(ref _receiveUnparkedTimestamp) >= timestamp;
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

                    if (Volatile.Read(ref _receiveParked) != 0)
                    {
                        // The application is applying backpressure; a pong could not be read anyway.
                        Interlocked.Increment(ref _keepAliveBackpressureSkips);
                        await Task.Delay(_options.PingInterval, ct).ConfigureAwait(false);
                        continue;
                    }

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
                            await WriteFrameLockedAsync(
                                OutboundFrame.Control(
                                    WebSocketOpcode.Ping,
                                    new ReadOnlyMemory<byte>(pingPayload, 0, 8)),
                                allowClosingState: false,
                                applicationMessage: false,
                                messageStarted: false,
                                ct).ConfigureAwait(false);

                            _metrics?.RecordFrameSent(CalculateFrameWireLength(8, masked: true));
                            _metrics?.RecordPingSent();
                            TryPublishMetricsUpdate();
                        }
                        catch (Exception ex) when (
                            (ex is InvalidOperationException || ex is WebSocketException) &&
                            State != WebSocketState.Open)
                        {
                            // Closing or closed meanwhile: not a keep-alive failure.
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
                        if (ReceiveLoopParkedSince(pingTimestamp))
                        {
                            // The pong may sit unread behind backpressured data: not evidence of a dead peer.
                            Interlocked.Increment(ref _keepAliveBackpressureSkips);
                            continue;
                        }

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
