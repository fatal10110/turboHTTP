using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Stateful WebSocket connection that owns stream I/O, state transitions, and close semantics.
    /// </summary>
    public sealed class WebSocketConnection : IDisposable, IAsyncDisposable
    {
        private static readonly IReadOnlyDictionary<WebSocketState, WebSocketState[]> AllowedTransitions =
            new Dictionary<WebSocketState, WebSocketState[]>
            {
                [WebSocketState.None] = new[] { WebSocketState.Connecting, WebSocketState.Closed },
                [WebSocketState.Connecting] = new[] { WebSocketState.Open, WebSocketState.Closed },
                [WebSocketState.Open] = new[] { WebSocketState.Closing, WebSocketState.Closed },
                [WebSocketState.Closing] = new[] { WebSocketState.Closed },
                [WebSocketState.Closed] = Array.Empty<WebSocketState>()
            };

        private static readonly double StopwatchFrequency = System.Diagnostics.Stopwatch.Frequency;

        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly TaskCompletionSource<WebSocketCloseStatus> _remoteCloseTcs =
            new TaskCompletionSource<WebSocketCloseStatus>(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _state;
        private int _disposed;
        private int _closeFrameSent;
        private int _finalized;

        private Stream _stream;
        private IWebSocketTransport _transport;
        private WebSocketConnectionOptions _options;
        private WebSocketFrameReader _frameReader;
        private WebSocketFrameWriter _frameWriter;
        private MessageAssembler _messageAssembler;
        private BoundedAsyncQueue<WebSocketMessage> _receiveQueue;

        private CancellationTokenSource _lifecycleCts;
        private CancellationTokenSource _keepAliveCts;
        private Task _receiveLoopTask;
        private Task _keepAliveTask;

        private Exception _terminalError;
        private WebSocketCloseStatus _closeStatus;
        private bool _hasCloseStatus;

        private string _subProtocol;
        private IReadOnlyList<string> _negotiatedExtensions = Array.Empty<string>();

        private Uri _remoteUri;
        private DateTimeOffset _connectedAtUtc;

        private long _lastActivityStopwatchTimestamp;
        private long _lastActivityUtcTicks;
        private long _lastApplicationMessageStopwatchTimestamp;
        private long _lastPongTimestamp;
        private long _lastPingTimestamp;
        private long _pingCounter;
        private long _lastPongCounter;
        private TaskCompletionSource<long> _pongWaiter;

        public event Action<WebSocketConnection, WebSocketState> StateChanged;

        public event Action<WebSocketConnection, Exception> Error;

        public WebSocketState State => (WebSocketState)Volatile.Read(ref _state);

        public Uri RemoteUri => _remoteUri;

        public string SubProtocol => _subProtocol;

        public IReadOnlyList<string> NegotiatedExtensions => _negotiatedExtensions;

        public DateTimeOffset ConnectedAtUtc => _connectedAtUtc;

        public DateTimeOffset LastActivityUtc
        {
            get
            {
                long ticks = Volatile.Read(ref _lastActivityUtcTicks);
                return ticks > 0
                    ? new DateTimeOffset(ticks, TimeSpan.Zero)
                    : default;
            }
        }

        public Exception TerminalError => _terminalError;

        public WebSocketCloseStatus? CloseStatus => _hasCloseStatus ? _closeStatus : (WebSocketCloseStatus?)null;

        public async Task ConnectAsync(
            Uri uri,
            IWebSocketTransport transport,
            WebSocketConnectionOptions options,
            CancellationToken ct)
        {
            ThrowIfDisposed();

            if (uri == null)
                throw new ArgumentNullException(nameof(uri));
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            if (!TryTransitionState(WebSocketState.None, WebSocketState.Connecting))
            {
                throw new InvalidOperationException(
                    "ConnectAsync requires the connection to be in None state. Current state: " + State + ".");
            }

            options = options?.Clone() ?? new WebSocketConnectionOptions();
            options.Validate();

            _transport = transport;
            _options = options;
            _frameReader = new WebSocketFrameReader(options.MaxFrameSize);
            _frameWriter = new WebSocketFrameWriter(options.FragmentationThreshold);
            _messageAssembler = new MessageAssembler(options.MaxMessageSize, options.MaxFragmentCount);
            _receiveQueue = new BoundedAsyncQueue<WebSocketMessage>(options.ReceiveQueueCapacity);
            _lifecycleCts = new CancellationTokenSource();

            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(options.HandshakeTimeout);

                var handshakeToken = handshakeCts.Token;

                _stream = await transport.ConnectAsync(uri, options, handshakeToken).ConfigureAwait(false);

                var handshakeRequest = WebSocketHandshake.BuildRequest(
                    uri,
                    options.SubProtocols,
                    options.Extensions,
                    options.CustomHeaders);

                await WebSocketHandshake.WriteRequestAsync(_stream, handshakeRequest, handshakeToken)
                    .ConfigureAwait(false);

                var handshakeResult = await WebSocketHandshakeValidator.ValidateAsync(
                    _stream,
                    handshakeRequest,
                    handshakeToken).ConfigureAwait(false);

                if (!handshakeResult.Success)
                    throw new WebSocketHandshakeException(handshakeResult);

                _remoteUri = uri;
                _subProtocol = handshakeResult.NegotiatedSubProtocol;
                _negotiatedExtensions = handshakeResult.NegotiatedExtensions;
                _connectedAtUtc = DateTimeOffset.UtcNow;

                if (handshakeResult.PrefetchedBytes.Length > 0)
                {
                    _stream = new PrefetchedStream(_stream, handshakeResult.PrefetchedBytes);
                }

                TouchActivity(applicationMessage: false);
                Interlocked.Exchange(ref _lastPongTimestamp, System.Diagnostics.Stopwatch.GetTimestamp());

                if (!TryTransitionState(WebSocketState.Connecting, WebSocketState.Open))
                {
                    throw new InvalidOperationException(
                        "Failed to transition connection from Connecting to Open. Current state: " + State + ".");
                }

                _receiveLoopTask = ReceiveLoopAsync(_lifecycleCts.Token);

                if (_options.PingInterval > TimeSpan.Zero)
                {
                    _keepAliveCts = CancellationTokenSource.CreateLinkedTokenSource(_lifecycleCts.Token);
                    _keepAliveTask = RunKeepAliveLoopAsync(_keepAliveCts.Token);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                FinalizeClose(
                    new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                    new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket connect was canceled."));
                await ObserveReceiveLoopTerminationAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                WebSocketException mapped;
                if (ex is WebSocketHandshakeException)
                {
                    mapped = new WebSocketException(WebSocketError.HandshakeFailed, ex.Message, ex);
                }
                else
                {
                    mapped = new WebSocketException(WebSocketError.ConnectionClosed, ex.Message, ex);
                }

                FinalizeClose(
                    new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                    mapped);
                await ObserveReceiveLoopTerminationAsync().ConfigureAwait(false);
                throw mapped;
            }
        }

        public async Task SendTextAsync(string message, CancellationToken ct)
        {
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            await SendLockedAsync(
                allowClosingState: false,
                applicationMessage: true,
                async (stream, token) =>
                {
                    await _frameWriter.WriteTextAsync(stream, message, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        public async Task SendTextAsync(ReadOnlyMemory<byte> utf8Payload, CancellationToken ct)
        {
            await SendLockedAsync(
                allowClosingState: false,
                applicationMessage: true,
                async (stream, token) =>
                {
                    await _frameWriter.WriteMessageAsync(stream, WebSocketOpcode.Text, utf8Payload, token)
                        .ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
        }

        public async Task SendBinaryAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            await SendLockedAsync(
                allowClosingState: false,
                applicationMessage: true,
                async (stream, token) =>
                {
                    await _frameWriter.WriteBinaryAsync(stream, payload, token).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);
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

        public void Abort()
        {
            if (State == WebSocketState.Closed)
                return;

            FinalizeClose(
                new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket connection aborted."));
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Abort();

            _sendLock.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                if (State == WebSocketState.Open || State == WebSocketState.Closing)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await CloseAsync(WebSocketCloseCode.NormalClosure, "Disposing connection.", cts.Token)
                        .ConfigureAwait(false);
                }
                else
                {
                    Abort();
                }
            }
            catch
            {
                Abort();
            }
            finally
            {
                _sendLock.Dispose();
            }
        }

        private bool TryTransitionState(WebSocketState expected, WebSocketState next)
        {
            if (!IsTransitionAllowed(expected, next))
                return false;

            int previous = Interlocked.CompareExchange(ref _state, (int)next, (int)expected);
            if (previous != (int)expected)
                return false;

            var handler = StateChanged;
            handler?.Invoke(this, next);
            return true;
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
                        string decodedText = null;
                        var buffer = assembledMessage.DetachPayloadBuffer(out int payloadLength);

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
                                    System.Buffers.ArrayPool<byte>.Shared.Return(buffer);

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
                    reason = WebSocketConstants.StrictUtf8.GetString(
                        payload.Slice(2).Span.ToArray(),
                        0,
                        payload.Length - 2);
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

                    byte[] pingPayload = System.Buffers.ArrayPool<byte>.Shared.Rent(8);
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
                        }
                        catch (InvalidOperationException) when (State != WebSocketState.Open)
                        {
                            break;
                        }
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(pingPayload);
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

        private void FinalizeClose(WebSocketCloseStatus status, Exception terminalError)
        {
            if (Interlocked.CompareExchange(ref _finalized, 1, 0) != 0)
                return;

            _hasCloseStatus = true;
            _closeStatus = status;

            if (terminalError != null)
            {
                _terminalError = terminalError;
                var errorHandler = Error;
                errorHandler?.Invoke(this, terminalError);
            }

            _remoteCloseTcs.TrySetResult(status);

            _keepAliveCts?.Cancel();
            _lifecycleCts?.Cancel();
            Interlocked.Exchange(ref _pongWaiter, null)?.TrySetCanceled();

            _messageAssembler?.Reset();
            _receiveQueue?.Complete(terminalError);

            var state = State;
            while (state != WebSocketState.Closed)
            {
                if (state == WebSocketState.Open)
                {
                    if (TryTransitionState(WebSocketState.Open, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.Closing)
                {
                    if (TryTransitionState(WebSocketState.Closing, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.Connecting)
                {
                    if (TryTransitionState(WebSocketState.Connecting, WebSocketState.Closed))
                        break;
                }
                else if (state == WebSocketState.None)
                {
                    if (TryTransitionState(WebSocketState.None, WebSocketState.Closed))
                        break;
                }
                else
                {
                    break;
                }

                state = State;
            }

            SafeDispose(_stream);
            _stream = null;

            SafeDispose(_frameWriter);
            _frameWriter = null;

            _keepAliveCts?.Dispose();
            _keepAliveCts = null;

            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }

        private static void SafeDispose(IDisposable disposable)
        {
            if (disposable == null)
                return;

            try
            {
                disposable.Dispose();
            }
            catch
            {
                // Best effort shutdown path.
            }
        }

        private void TouchActivity(bool applicationMessage)
        {
            long nowStopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
            long nowUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

            Interlocked.Exchange(ref _lastActivityStopwatchTimestamp, nowStopwatch);
            Interlocked.Exchange(ref _lastActivityUtcTicks, nowUtcTicks);

            if (applicationMessage || Volatile.Read(ref _lastApplicationMessageStopwatchTimestamp) == 0)
                Interlocked.Exchange(ref _lastApplicationMessageStopwatchTimestamp, nowStopwatch);
        }

        private static TimeSpan ElapsedSinceStopwatch(long fromTimestamp)
        {
            if (fromTimestamp <= 0)
                return TimeSpan.MaxValue;

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long delta = now - fromTimestamp;
            if (delta <= 0)
                return TimeSpan.Zero;

            double seconds = delta / StopwatchFrequency;
            return TimeSpan.FromSeconds(seconds);
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken ct)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancellationTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(
                static state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                cancellationTcs);

            var completed = await Task.WhenAny(task, cancellationTcs.Task).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
                throw new OperationCanceledException(ct);

            await task.ConfigureAwait(false);
        }

        private async Task ObserveReceiveLoopTerminationAsync()
        {
            var receiveLoopTask = _receiveLoopTask;
            if (receiveLoopTask == null)
                return;

            var completed = await Task.WhenAny(receiveLoopTask, Task.Delay(TimeSpan.FromSeconds(1)))
                .ConfigureAwait(false);

            if (!ReferenceEquals(completed, receiveLoopTask))
                return;

            try
            {
                await receiveLoopTask.ConfigureAwait(false);
            }
            catch
            {
                // FinalizeClose already recorded the terminal error path.
            }
        }

        private static bool IsTransitionAllowed(WebSocketState expected, WebSocketState next)
        {
            if (!AllowedTransitions.TryGetValue(expected, out var allowed))
                return false;

            for (int i = 0; i < allowed.Length; i++)
            {
                if (allowed[i] == next)
                    return true;
            }

            return false;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(WebSocketConnection));
        }

        /// <summary>
        /// Read-through wrapper that drains prefetched bytes before delegating to the inner stream.
        /// </summary>
        private sealed class PrefetchedStream : Stream
        {
            private readonly Stream _inner;
            private readonly byte[] _prefetched;
            private int _prefetchedOffset;
            private int _disposed;

            public PrefetchedStream(Stream inner, byte[] prefetched)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _prefetched = prefetched ?? Array.Empty<byte>();
            }

            public override bool CanRead => _inner.CanRead;

            public override bool CanSeek => false;

            public override bool CanWrite => _inner.CanWrite;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                _inner.Flush();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                return _inner.FlushAsync(cancellationToken);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (TryReadPrefetched(buffer, offset, count, out var read))
                    return read;

                return _inner.Read(buffer, offset, count);
            }

            public override async Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                if (TryReadPrefetched(buffer, offset, count, out var read))
                    return read;

                return await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }

            public override ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                if (TryReadPrefetched(buffer.Span, out var read))
                    return new ValueTask<int>(read);

                return _inner.ReadAsync(buffer, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _inner.Write(buffer, offset, count);
            }

            public override Task WriteAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken)
            {
                return _inner.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override ValueTask WriteAsync(
                ReadOnlyMemory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                return _inner.WriteAsync(buffer, cancellationToken);
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    base.Dispose(disposing);
                    return;
                }

                if (disposing)
                    _inner.Dispose();

                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;

                await _inner.DisposeAsync().ConfigureAwait(false);
                await base.DisposeAsync().ConfigureAwait(false);
            }

            private bool TryReadPrefetched(byte[] buffer, int offset, int count, out int read)
            {
                if (_prefetchedOffset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(count, _prefetched.Length - _prefetchedOffset);
                Buffer.BlockCopy(_prefetched, _prefetchedOffset, buffer, offset, read);
                _prefetchedOffset += read;
                return true;
            }

            private bool TryReadPrefetched(Span<byte> destination, out int read)
            {
                if (_prefetchedOffset >= _prefetched.Length)
                {
                    read = 0;
                    return false;
                }

                read = Math.Min(destination.Length, _prefetched.Length - _prefetchedOffset);
                _prefetched.AsSpan(_prefetchedOffset, read).CopyTo(destination);
                _prefetchedOffset += read;
                return true;
            }
        }

        private sealed class AsyncQueueCompletedException : Exception
        {
            public AsyncQueueCompletedException(Exception innerException)
                : base("Queue is completed.", innerException)
            {
            }
        }

        /// <summary>
        /// Bounded async queue implementation used to provide receive-side backpressure
        /// without taking a dependency on System.Threading.Channels in netstandard2.1.
        /// </summary>
        private sealed class BoundedAsyncQueue<T>
        {
            private readonly Queue<T> _queue = new Queue<T>();
            private readonly SemaphoreSlim _items = new SemaphoreSlim(0);
            private readonly SemaphoreSlim _spaces;
            private readonly object _gate = new object();
            private readonly int _capacity;

            private int _waitingReaders;
            private bool _completed;
            private Exception _completionError;

            public BoundedAsyncQueue(int capacity)
            {
                if (capacity < 1)
                    throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be > 0.");

                _capacity = capacity;
                _spaces = new SemaphoreSlim(capacity, capacity);
            }

            public async ValueTask EnqueueAsync(T value, CancellationToken ct)
            {
                await _spaces.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    lock (_gate)
                    {
                        if (_completed)
                            throw new AsyncQueueCompletedException(_completionError);

                        _queue.Enqueue(value);
                    }

                    _items.Release();
                }
                catch
                {
                    _spaces.Release();
                    throw;
                }
            }

            public async ValueTask<T> DequeueAsync(CancellationToken ct)
            {
                while (true)
                {
                    lock (_gate)
                    {
                        if (_queue.Count > 0)
                        {
                            var item = _queue.Dequeue();
                            _spaces.Release();
                            return item;
                        }

                        if (_completed)
                            throw new AsyncQueueCompletedException(_completionError);

                        _waitingReaders++;
                    }

                    try
                    {
                        await _items.WaitAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _waitingReaders--;
                        }
                    }
                }
            }

            public void Complete(Exception error)
            {
                lock (_gate)
                {
                    if (_completed)
                        return;

                    _completed = true;
                    _completionError = error;

                    if (_waitingReaders > 0)
                        _items.Release(_waitingReaders);
                }
            }
        }
    }
}
