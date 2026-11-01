using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Stateful WebSocket connection that owns stream I/O, state transitions, and close semantics.
    /// </summary>
    public sealed partial class WebSocketConnection : IDisposable, IAsyncDisposable
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

        // Receive-loop backpressure (#78): 1 while the read loop waits for space in _receiveQueue,
        // plus the Stopwatch timestamp of the last time such a wait ended. Interlocked/Volatile owned.
        private int _receiveParked;
        private long _receiveUnparkedTimestamp;

        // Keep-alive iterations that skipped a ping or forgave a pong timeout because of local backpressure.
        // Interlocked owned; diagnostics and tests only.
        private long _keepAliveBackpressureSkips;
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

        /// <summary>
        /// Number of keep-alive iterations that skipped a ping or forgave a pong timeout because the read
        /// loop was parked on the full receive queue (#78).
        /// </summary>
        internal long KeepAliveBackpressureSkips => Interlocked.Read(ref _keepAliveBackpressureSkips);

        // Diagnostics and tests only (#80): receive-loop parking and the delivery queue's byte budget.
        internal bool IsReceiveLoopParked => Volatile.Read(ref _receiveParked) != 0;

        internal int ReceiveQueueCount => _receiveQueue?.Count ?? 0;

        internal long ReceiveQueueBytes => _receiveQueue?.QueuedBytes ?? 0;

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
            _receiveQueue = WebSocketMessageQueue.Create(options);
            _lifecycleCts = new CancellationTokenSource();

            List<IWebSocketExtension> configuredExtensions = null;
            var handshakeToken = CancellationToken.None;
            var disposeOnCancel = default(CancellationTokenRegistration);
            try
            {
                using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshakeCts.CancelAfter(options.HandshakeTimeout);

                handshakeToken = handshakeCts.Token;

                _stream = await transport.ConnectAsync(uri, options, handshakeToken).ConfigureAwait(false);

                // #226: Mono/IL2CPP NetworkStream ignores the token once a read is pending, so a server
                // that accepts and never sends the 101 would hold this call past HandshakeTimeout.
                // Disposing the stream on cancellation faults the pending read or write; the fault is
                // reported as cancellation (or the handshake timeout) below. A token that already fired
                // disposes the stream immediately.
                disposeOnCancel = RegisterDisposeOnCancel(_stream, handshakeToken);

                configuredExtensions = CreateConnectionExtensions(options);

                // Built even without configured extensions: a server that selects any extension the
                // client did not offer must fail the connection (RFC 6455 section 4.1, #81).
                var extensionNegotiator = new WebSocketExtensionNegotiator(configuredExtensions);

                var requestedExtensions = BuildRequestedExtensions(extensionNegotiator);

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

                var extensionNegotiationResult = extensionNegotiator.ProcessNegotiation(serverExtensionsHeader);

                // An unoffered, duplicate or unhonourable extension in the response always fails the
                // connection, whether or not extensions are required (RFC 6455 sections 4.1 and 9.1).
                if (!extensionNegotiationResult.IsSuccess)
                {
                    string message = "Server extension response rejected: " + extensionNegotiationResult.ErrorMessage;

                    await TrySendExtensionFailureCloseAsync(
                        WebSocketCloseCode.ProtocolError,
                        message,
                        handshakeToken).ConfigureAwait(false);

                    throw CreateExtensionNegotiationException(WebSocketCloseCode.ProtocolError, message);
                }

                if (options.RequireNegotiatedExtensions &&
                    extensionNegotiationResult.ActiveExtensions.Count == 0)
                {
                    const string message =
                        "Server did not negotiate any required WebSocket extension.";

                    await TrySendExtensionFailureCloseAsync(
                        WebSocketCloseCode.MandatoryExtension,
                        message,
                        handshakeToken).ConfigureAwait(false);
                    throw CreateExtensionNegotiationException(WebSocketCloseCode.MandatoryExtension, message);
                }

                // The opening handshake is complete: the stream is now owned by the receive loop and
                // FinalizeClose. A token that fired before the registration was removed may have
                // disposed it, so the connection never opens on it.
                disposeOnCancel.Dispose();
                handshakeToken.ThrowIfCancellationRequested();

                _remoteUri = uri;
                _subProtocol = handshakeResult.NegotiatedSubProtocol;
                _activeExtensions = extensionNegotiationResult.ActiveExtensions;
                _negotiatedExtensions = BuildNegotiatedExtensionNames(_activeExtensions);
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
                _frameReader = new WebSocketFrameReader(
                    options.MaxFrameSize,
                    _allowedRsvMask,
                    maxMessageSize: options.MaxMessageSize);

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
            catch (Exception ex) when (ct.IsCancellationRequested &&
                                       (ex is OperationCanceledException || handshakeToken.IsCancellationRequested))
            {
                // The caller canceled; a disposed-stream fault caused by that cancellation is the
                // same outcome (#226).
                disposeOnCancel.Dispose();
                DisposeExtensions(configuredExtensions);
                FinalizeClose(
                    new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure),
                    new WebSocketException(WebSocketError.ConnectionClosed, "WebSocket connect was canceled."));
                await ObserveReceiveLoopTerminationAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException)
                    throw;

                throw new OperationCanceledException("WebSocket connect was canceled.", ex, ct);
            }
            catch (Exception ex)
            {
                disposeOnCancel.Dispose();
                DisposeExtensions(configuredExtensions);
                WebSocketException mapped;
                if (handshakeToken.IsCancellationRequested)
                {
                    // HandshakeTimeout expired (the caller token did not fire): the connect phase,
                    // CONNECT tunnel, TLS or the upgrade exchange did not finish in time.
                    mapped = new WebSocketException(
                        WebSocketError.ConnectionClosed,
                        "WebSocket opening handshake timed out after " +
                        options.HandshakeTimeout.TotalMilliseconds + " ms.",
                        ex);
                }
                else if (ex is WebSocketException wsEx)
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

        private static CancellationTokenRegistration RegisterDisposeOnCancel(Stream stream, CancellationToken ct)
        {
            if (stream == null || !ct.CanBeCanceled)
                return default;

            return ct.Register(static state => SafeDispose((Stream)state), stream);
        }

        private async Task TrySendExtensionFailureCloseAsync(
            WebSocketCloseCode closeCode,
            string reason,
            CancellationToken ct)
        {
            var stream = _stream;
            var frameWriter = _frameWriter;
            if (stream == null || frameWriter == null)
                return;

            try
            {
                await frameWriter.WriteCloseAsync(
                    stream,
                    closeCode,
                    reason ?? string.Empty,
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort close signaling during failed extension negotiation.
            }
        }

        private static WebSocketException CreateExtensionNegotiationException(
            WebSocketCloseCode closeCode,
            string message)
        {
            var effectiveMessage = string.IsNullOrWhiteSpace(message)
                ? "WebSocket extension negotiation failed."
                : message;

            return new WebSocketException(
                WebSocketError.ExtensionNegotiationFailed,
                effectiveMessage,
                closeCode: closeCode,
                closeReason: effectiveMessage);
        }

    }
}
