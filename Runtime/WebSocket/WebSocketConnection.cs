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

    }
}
