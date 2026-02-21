using System;
using System.Buffers;
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
        private IReadOnlyList<IWebSocketExtension> _activeExtensions = Array.Empty<IWebSocketExtension>();
        private byte _allowedRsvMask;

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
        private WebSocketMetricsCollector _metrics;
        private WebSocketHealthMonitor _healthMonitor;

        public event Action<WebSocketConnection, WebSocketState> StateChanged;

        public event Action<WebSocketConnection, Exception> Error;

        public event Action<WebSocketConnection, WebSocketMetrics> MetricsUpdated;

        public event Action<WebSocketConnection, ConnectionQuality> ConnectionQualityChanged;

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

        public WebSocketMetrics Metrics => _metrics?.GetSnapshot() ?? default;

        public WebSocketHealthSnapshot Health => _healthMonitor?.GetSnapshot() ?? WebSocketHealthSnapshot.Unknown;

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
            _frameWriter = new WebSocketFrameWriter(options.FragmentationThreshold);
            _messageAssembler = new MessageAssembler(options.MaxMessageSize, options.MaxFragmentCount);
            _receiveQueue = new BoundedAsyncQueue<WebSocketMessage>(options.ReceiveQueueCapacity);
            _lifecycleCts = new CancellationTokenSource();

            List<IWebSocketExtension> configuredExtensions = null;
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(options.HandshakeTimeout);

                var handshakeToken = handshakeCts.Token;

                _stream = await transport.ConnectAsync(uri, options, handshakeToken).ConfigureAwait(false);

                configuredExtensions = CreateConnectionExtensions(options);
                var extensionNegotiator = configuredExtensions.Count > 0
                    ? new WebSocketExtensionNegotiator(configuredExtensions)
                    : null;

                var requestedExtensions = BuildRequestedExtensions(options.Extensions, extensionNegotiator);

                var handshakeRequest = WebSocketHandshake.BuildRequest(
                    uri,
                    options.SubProtocols,
                    requestedExtensions,
                    options.CustomHeaders);

                await WebSocketHandshake.WriteRequestAsync(_stream, handshakeRequest, handshakeToken)
                    .ConfigureAwait(false);

                var handshakeResult = await WebSocketHandshakeValidator.ValidateAsync(
                    _stream,
                    handshakeRequest,
                    handshakeToken).ConfigureAwait(false);

                if (!handshakeResult.Success)
                    throw new WebSocketHandshakeException(handshakeResult);

                string serverExtensionsHeader = JoinHeaderValues(
                    handshakeResult.ResponseHeaders.GetValues("Sec-WebSocket-Extensions"));

                var extensionNegotiationResult = extensionNegotiator != null
                    ? extensionNegotiator.ProcessNegotiation(serverExtensionsHeader)
                    : WebSocketExtensionNegotiationResult.Success(Array.Empty<IWebSocketExtension>(), 0);

                if (extensionNegotiator != null && !extensionNegotiationResult.IsSuccess)
                {
                    if (options.RequireNegotiatedExtensions)
                    {
                        await TrySendMandatoryExtensionCloseAsync(
                            extensionNegotiationResult.ErrorMessage,
                            handshakeToken).ConfigureAwait(false);

                        throw CreateMandatoryExtensionException(extensionNegotiationResult.ErrorMessage);
                    }

                    extensionNegotiationResult = WebSocketExtensionNegotiationResult.Success(
                        Array.Empty<IWebSocketExtension>(),
                        allowedRsvMask: 0);
                }

                if (extensionNegotiator != null &&
                    options.RequireNegotiatedExtensions &&
                    extensionNegotiationResult.ActiveExtensions.Count == 0)
                {
                    const string message =
                        "Server did not negotiate any required WebSocket extension.";

                    await TrySendMandatoryExtensionCloseAsync(message, handshakeToken).ConfigureAwait(false);
                    throw CreateMandatoryExtensionException(message);
                }

                _remoteUri = uri;
                _subProtocol = handshakeResult.NegotiatedSubProtocol;
                _activeExtensions = extensionNegotiationResult.ActiveExtensions;
                _negotiatedExtensions = extensionNegotiator != null
                    ? BuildNegotiatedExtensionNames(_activeExtensions)
                    : handshakeResult.NegotiatedExtensions;
                DisposeUnselectedExtensions(configuredExtensions, _activeExtensions);
                configuredExtensions = null;
                _allowedRsvMask = extensionNegotiationResult.AllowedRsvMask;
                _metrics = new WebSocketMetricsCollector();
                _healthMonitor = options.EnableHealthMonitoring
                    ? new WebSocketHealthMonitor()
                    : null;
                if (_healthMonitor != null)
                    _healthMonitor.OnQualityChanged += HandleHealthQualityChanged;
                _connectedAtUtc = DateTimeOffset.UtcNow;
                _frameReader = new WebSocketFrameReader(options.MaxFrameSize, _allowedRsvMask);

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
                DisposeExtensions(configuredExtensions);
                FinalizeClose(
                    new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                    new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket connect was canceled."));
                await ObserveReceiveLoopTerminationAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                DisposeExtensions(configuredExtensions);
                WebSocketException mapped;
                if (ex is WebSocketException wsEx)
                {
                    mapped = wsEx;
                }
                else if (ex is WebSocketHandshakeException)
                {
                    mapped = new WebSocketException(WebSocketError.HandshakeFailed, ex.Message, ex);
                }
                else
                {
                    mapped = new WebSocketException(WebSocketError.ConnectionClosed, ex.Message, ex);
                }

                var closeStatus = mapped.CloseCode.HasValue
                    ? new WebSocketCloseStatus(mapped.CloseCode.Value, mapped.CloseReason ?? string.Empty)
                    : new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure);

                FinalizeClose(
                    closeStatus,
                    mapped);
                await ObserveReceiveLoopTerminationAsync().ConfigureAwait(false);
                throw mapped;
            }
        }

        private async Task TrySendMandatoryExtensionCloseAsync(string reason, CancellationToken ct)
        {
            var stream = _stream;
            var frameWriter = _frameWriter;
            if (stream == null || frameWriter == null)
                return;

            try
            {
                await frameWriter.WriteCloseAsync(
                    stream,
                    WebSocketCloseCode.MandatoryExtension,
                    reason ?? string.Empty,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close signaling during failed extension negotiation.
            }
        }

        private static WebSocketException CreateMandatoryExtensionException(string message)
        {
            var effectiveMessage = string.IsNullOrWhiteSpace(message)
                ? "Required WebSocket extension negotiation failed."
                : message;

            return new WebSocketException(
                WebSocketError.ExtensionNegotiationFailed,
                effectiveMessage,
                closeCode: WebSocketCloseCode.MandatoryExtension,
                closeReason: effectiveMessage);
        }

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

        public void Abort()
        {
            AbortCore(validateDisposed: true);
        }

        private void AbortCore(bool validateDisposed)
        {
            if (validateDisposed)
                ThrowIfDisposed();

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

            AbortCore(validateDisposed: false);

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
                    AbortCore(validateDisposed: false);
                }
            }
            catch
            {
                AbortCore(validateDisposed: false);
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

        private async Task WriteMessageWithExtensionsAsync(
            Stream stream,
            WebSocketOpcode opcode,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct)
        {
            ReadOnlyMemory<byte> transformedPayload = payload;
            IMemoryOwner<byte> currentOwner = null;
            byte rsvBits = 0;

            try
            {
                for (int i = 0; i < _activeExtensions.Count; i++)
                {
                    var extension = _activeExtensions[i];
                    if (extension == null)
                        continue;

                    var transformed = extension.TransformOutbound(transformedPayload, opcode, out byte extensionRsvBits);

                    if ((extensionRsvBits & ~extension.RsvBitMask) != 0)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProtocolViolation,
                            "Extension attempted to set RSV bits outside its declared mask.");
                    }

                    if ((rsvBits & extensionRsvBits) != 0)
                    {
                        throw new WebSocketException(
                            WebSocketError.ProtocolViolation,
                            "Multiple extensions attempted to set overlapping RSV bits.");
                    }

                    rsvBits |= extensionRsvBits;

                    if (transformed == null)
                        continue;

                    currentOwner?.Dispose();
                    currentOwner = transformed;
                    transformedPayload = transformed.Memory;
                }

                await _frameWriter.WriteMessageAsync(stream, opcode, transformedPayload, ct, rsvBits)
                    .ConfigureAwait(false);

                CalculateMessageWireStats(
                    transformedPayload.Length,
                    _options.FragmentationThreshold,
                    out int frameCount,
                    out int byteCount);

                _metrics?.RecordFramesSent(frameCount, byteCount);
                _metrics?.RecordMessageSent();

                if (IsCompressionRsvBitSet(rsvBits))
                    _metrics?.RecordCompression(payload.Length, transformedPayload.Length);

                TryPublishMetricsUpdate();
            }
            finally
            {
                currentOwner?.Dispose();
            }
        }

        private IMemoryOwner<byte> ApplyInboundExtensions(WebSocketAssembledMessage assembledMessage)
        {
            if (assembledMessage == null)
                throw new ArgumentNullException(nameof(assembledMessage));

            if (_activeExtensions.Count == 0 || assembledMessage.RsvBits == 0)
                return null;

            ReadOnlyMemory<byte> payload = assembledMessage.Payload;
            IMemoryOwner<byte> currentOwner = null;
            byte remainingRsvBits = assembledMessage.RsvBits;

            for (int i = _activeExtensions.Count - 1; i >= 0; i--)
            {
                var extension = _activeExtensions[i];
                if (extension == null)
                    continue;

                if ((remainingRsvBits & extension.RsvBitMask) == 0)
                    continue;

                var transformed = extension.TransformInbound(payload, assembledMessage.Opcode, remainingRsvBits);
                remainingRsvBits = (byte)(remainingRsvBits & ~extension.RsvBitMask);

                if (transformed == null)
                    continue;

                currentOwner?.Dispose();
                currentOwner = transformed;
                payload = transformed.Memory;
            }

            if (remainingRsvBits != 0)
            {
                currentOwner?.Dispose();
                throw new WebSocketProtocolException(
                    WebSocketError.ProtocolViolation,
                    "Incoming frame set RSV bits without a matching negotiated extension.");
            }

            return currentOwner;
        }

        private byte[] AcquireMessagePayloadBuffer(
            WebSocketAssembledMessage assembledMessage,
            IMemoryOwner<byte> transformedInbound,
            out int payloadLength)
        {
            if (transformedInbound == null)
                return assembledMessage.DetachPayloadBuffer(out payloadLength);

            var transformedMemory = transformedInbound.Memory;
            payloadLength = transformedMemory.Length;

            if (payloadLength > _options.MaxMessageSize)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressedMessageTooLarge,
                    "Decompressed payload exceeds configured MaxMessageSize.");
            }

            if (payloadLength == 0)
                return null;

            if (transformedInbound is ArrayPoolMemoryOwner<byte> poolOwner &&
                poolOwner.TryDetach(out var detachedBuffer, out int detachedLength))
            {
                payloadLength = detachedLength;
                return detachedBuffer;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(payloadLength);
            transformedMemory.CopyTo(new Memory<byte>(buffer, 0, payloadLength));
            return buffer;
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

            CancelAndDisposeAfterTaskCompletion(ref _keepAliveCts, _keepAliveTask);
            CancelAndDisposeAfterTaskCompletion(ref _lifecycleCts, _receiveLoopTask);
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

            DisposeExtensions(_activeExtensions);
            _activeExtensions = Array.Empty<IWebSocketExtension>();

            if (_healthMonitor != null)
            {
                _healthMonitor.OnQualityChanged -= HandleHealthQualityChanged;
                _healthMonitor = null;
            }
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

        private static void DisposeExtensions(IReadOnlyList<IWebSocketExtension> extensions)
        {
            if (extensions == null || extensions.Count == 0)
                return;

            for (int i = extensions.Count - 1; i >= 0; i--)
            {
                SafeDispose(extensions[i]);
            }
        }

        private static void DisposeUnselectedExtensions(
            IReadOnlyList<IWebSocketExtension> configuredExtensions,
            IReadOnlyList<IWebSocketExtension> selectedExtensions)
        {
            if (configuredExtensions == null || configuredExtensions.Count == 0)
                return;

            var selected = new HashSet<IWebSocketExtension>();
            if (selectedExtensions != null)
            {
                for (int i = 0; i < selectedExtensions.Count; i++)
                {
                    if (selectedExtensions[i] != null)
                        selected.Add(selectedExtensions[i]);
                }
            }

            for (int i = 0; i < configuredExtensions.Count; i++)
            {
                var extension = configuredExtensions[i];
                if (extension != null && !selected.Contains(extension))
                    SafeDispose(extension);
            }
        }

        private static List<IWebSocketExtension> CreateConnectionExtensions(WebSocketConnectionOptions options)
        {
            var result = new List<IWebSocketExtension>();
            if (options?.ExtensionFactories == null)
                return result;

            for (int i = 0; i < options.ExtensionFactories.Count; i++)
            {
                var factory = options.ExtensionFactories[i];
                if (factory == null)
                    continue;

                var extension = factory();
                if (extension == null)
                {
                    throw new InvalidOperationException(
                        "Extension factory at index " + i + " returned null.");
                }

                result.Add(extension);
            }

            return result;
        }

        private static IReadOnlyList<string> BuildRequestedExtensions(
            IReadOnlyList<string> rawExtensions,
            WebSocketExtensionNegotiator negotiator)
        {
            var result = new List<string>();

            if (negotiator != null)
            {
                string structuredOffers = negotiator.BuildOffersHeader();
                if (!string.IsNullOrWhiteSpace(structuredOffers))
                    result.Add(structuredOffers);
            }

            if (rawExtensions != null)
            {
                for (int i = 0; i < rawExtensions.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(rawExtensions[i]))
                        continue;

                    result.Add(rawExtensions[i]);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> BuildNegotiatedExtensionNames(
            IReadOnlyList<IWebSocketExtension> activeExtensions)
        {
            if (activeExtensions == null || activeExtensions.Count == 0)
                return Array.Empty<string>();

            var names = new List<string>(activeExtensions.Count);
            for (int i = 0; i < activeExtensions.Count; i++)
            {
                var extension = activeExtensions[i];
                if (extension == null || string.IsNullOrWhiteSpace(extension.Name))
                    continue;

                names.Add(extension.Name);
            }

            return names;
        }

        private static string JoinHeaderValues(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return string.Empty;

            if (values.Count == 1)
                return values[0] ?? string.Empty;

            return string.Join(", ", values);
        }

        private static void CancelAndDisposeAfterTaskCompletion(
            ref CancellationTokenSource ctsField,
            Task ownerTask)
        {
            var cts = Interlocked.Exchange(ref ctsField, null);
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch
            {
                // Cancellation source may already be disposed in racey shutdown paths.
            }

            if (ownerTask == null || ownerTask.IsCompleted)
            {
                cts.Dispose();
                return;
            }

            _ = ownerTask.ContinueWith(
                static (_, state) =>
                {
                    try
                    {
                        ((CancellationTokenSource)state).Dispose();
                    }
                    catch
                    {
                        // Best effort cleanup.
                    }
                },
                cts,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void TryPublishMetricsUpdate()
        {
            var metrics = _metrics;
            var options = _options;
            if (metrics == null || options == null)
                return;

            if (!metrics.ShouldPublishSnapshot(
                options.MetricsUpdateMessageInterval,
                options.MetricsUpdateInterval))
            {
                return;
            }

            var snapshot = metrics.GetSnapshot();
            _healthMonitor?.RecordMetricsSnapshot(snapshot);

            var handler = MetricsUpdated;
            handler?.Invoke(this, snapshot);
        }

        private void HandleHealthQualityChanged(ConnectionQuality quality)
        {
            var handler = ConnectionQualityChanged;
            handler?.Invoke(this, quality);
        }

        private bool IsCompressionRsvBitSet(byte rsvBits)
        {
            const byte rsv1Mask = 0x40;
            if ((rsvBits & rsv1Mask) == 0)
                return false;

            if (_activeExtensions == null || _activeExtensions.Count == 0)
                return false;

            for (int i = 0; i < _activeExtensions.Count; i++)
            {
                var extension = _activeExtensions[i];
                if (extension == null)
                    continue;

                if ((extension.RsvBitMask & rsv1Mask) == 0)
                    continue;

                if (string.Equals(extension.Name, "permessage-deflate", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static void CalculateMessageWireStats(
            int payloadLength,
            int fragmentationThreshold,
            out int frameCount,
            out int totalBytes)
        {
            if (payloadLength <= fragmentationThreshold)
            {
                frameCount = 1;
                totalBytes = CalculateFrameWireLength(payloadLength, masked: true);
                return;
            }

            int bytes = 0;
            int count = 0;
            int remaining = payloadLength;
            while (remaining > 0)
            {
                int fragmentLength = Math.Min(fragmentationThreshold, remaining);
                bytes = checked(bytes + CalculateFrameWireLength(fragmentLength, masked: true));
                count++;
                remaining -= fragmentLength;
            }

            frameCount = count;
            totalBytes = bytes;
        }

        private static int CalculateFrameWireLength(int payloadLength, bool masked)
        {
            int headerLength = 2;
            if (payloadLength > ushort.MaxValue)
            {
                headerLength += 8;
            }
            else if (payloadLength > 125)
            {
                headerLength += 2;
            }

            if (masked)
                headerLength += 4;

            return checked(headerLength + payloadLength);
        }

        private static int GetClosePayloadLength(string reason)
        {
            reason = reason ?? string.Empty;

            int reasonBytes = WebSocketConstants.GetTruncatedCloseReasonByteCount(reason, out _);
            return checked(2 + reasonBytes);
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

        private static TimeSpan ElapsedBetweenStopwatch(long startTimestamp, long endTimestamp)
        {
            if (startTimestamp <= 0 || endTimestamp <= startTimestamp)
                return TimeSpan.Zero;

            long delta = endTimestamp - startTimestamp;
            double seconds = delta / StopwatchFrequency;
            if (seconds <= 0)
                return TimeSpan.Zero;

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

    }
}
