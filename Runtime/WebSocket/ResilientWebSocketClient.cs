using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// IWebSocketClient wrapper with automatic reconnect support.
    /// </summary>
    public sealed class ResilientWebSocketClient : IWebSocketClient
    {
        private readonly object _sync = new object();
        private readonly IWebSocketTransport _providedTransport;

        private Uri _uri;
        private WebSocketConnectionOptions _options;
        private IWebSocketClient _client;
        private CancellationTokenSource _lifecycleCts;
        private Task _receivePumpTask;
        private BoundedAsyncQueue<WebSocketMessage> _incomingQueue;

        private int _disposed;
        private int _manualClose;
        private int _receiveInProgress;
        private int _receiveAllInProgress;
        private int _reconnectLoopActive;
        private int _closedEventRaised;
        private int _nextReconnectAttempt = 1;

        // Owned by the running reconnect loop (created, published and disposed by it); CloseAsync and Abort
        // only cancel it, so a caller's close stops the backoff delay and the replacement connect (#1105).
        private CancellationTokenSource _reconnectCts;

        // Guarded by _sync: a disconnect of the current connection that arrived while the reconnect loop
        // was active. The loop owner runs the loop again for it instead of releasing the loop (#417).
        private PendingDisconnect _pendingDisconnect;
        private WebSocketMetrics _latestMetrics;
        private WebSocketHealthSnapshot _latestHealth;

        /// <inheritdoc />
        public event Action OnConnected;
        /// <inheritdoc />
        public event Action<WebSocketMessage> OnMessage;
        /// <inheritdoc />
        public event Action<WebSocketException> OnError;
        /// <inheritdoc />
        public event Action<WebSocketCloseCode, string> OnClosed;
        /// <inheritdoc />
        public event Action<WebSocketMetrics> OnMetricsUpdated;
        /// <inheritdoc />
        public event Action<ConnectionQuality> OnConnectionQualityChanged;
        /// <summary>Raised before each reconnect attempt with the 1-based attempt number and the delay before it.</summary>
        public event Action<int, TimeSpan> OnReconnecting;
        /// <summary>Raised when a reconnect attempt has opened a new connection.</summary>
        public event Action OnReconnected;

        /// <summary>Creates a reconnecting client that uses the transport registered with <see cref="WebSocketTransportFactory"/>.</summary>
        public ResilientWebSocketClient()
        {
        }

        /// <summary>Creates a reconnecting client over <paramref name="transport"/>; the caller keeps ownership of the transport.</summary>
        public ResilientWebSocketClient(IWebSocketTransport transport)
        {
            _providedTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <inheritdoc />
        public WebSocketState State
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return WebSocketState.Closed;

                if (Volatile.Read(ref _reconnectLoopActive) != 0)
                    return WebSocketState.Connecting;

                var client = Volatile.Read(ref _client);
                return client?.State ?? WebSocketState.None;
            }
        }

        /// <inheritdoc />
        public string SubProtocol => Volatile.Read(ref _client)?.SubProtocol;

        /// <inheritdoc />
        public WebSocketMetrics Metrics
        {
            get
            {
                var client = Volatile.Read(ref _client);
                return client?.Metrics ?? _latestMetrics;
            }
        }

        /// <inheritdoc />
        public WebSocketHealthSnapshot Health
        {
            get
            {
                var client = Volatile.Read(ref _client);
                return client?.Health ?? _latestHealth;
            }
        }

        /// <inheritdoc />
        public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            return ConnectAsync(uri, new WebSocketConnectionOptions(), ct);
        }

        /// <inheritdoc />
        public async Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            options = options?.Clone() ?? new WebSocketConnectionOptions();
            options.Validate();

            BoundedAsyncQueue<WebSocketMessage> previousQueue;
            lock (_sync)
            {
                if (_client != null && (_client.State == WebSocketState.Open || _client.State == WebSocketState.Connecting))
                {
                    throw new InvalidOperationException(
                        "Client is already connected or connecting. Current state: " + _client.State + ".");
                }

                _uri = uri;
                _options = options;
                previousQueue = _incomingQueue;
                _incomingQueue = WebSocketMessageQueue.Create(options);
                _lifecycleCts?.Dispose();
                _lifecycleCts = new CancellationTokenSource();
                _closedEventRaised = 0;
                _manualClose = 0;
                _nextReconnectAttempt = 1;
                _latestMetrics = default;
                _latestHealth = WebSocketHealthSnapshot.Unknown;
            }

            // A connection that ended on its own leaves its unread messages readable (#1106); they belong to
            // the previous connection, so a new ConnectAsync releases them.
            CompleteAndDrainQueue(previousQueue, null);

            await ConnectReplacementClientAsync(initialConnect: true, ct).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var client = RequireOpenClient();
            try
            {
                await client.SendAsync(message, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                throw CreateReplacedClientException(ex);
            }
        }

        /// <inheritdoc />
        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var client = RequireOpenClient();
            try
            {
                await client.SendAsync(data, ct).ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                throw CreateReplacedClientException(ex);
            }
        }

        /// <inheritdoc />
        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return SendAsync(new ReadOnlyMemory<byte>(data), ct);
        }

        /// <inheritdoc />
        public async ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
        {
            return await ReceiveCoreAsync(ct, fromAsyncEnumerable: false).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var state = State;
            if (state == WebSocketState.None)
            {
                throw new InvalidOperationException(
                    "ReceiveAllAsync requires Open, Connecting, Closing, or Closed state. Current state: " + state + ".");
            }

            if (Interlocked.CompareExchange(ref _receiveAllInProgress, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent ReceiveAllAsync calls are not supported.");

            if (Volatile.Read(ref _receiveInProgress) != 0)
            {
                Interlocked.Exchange(ref _receiveAllInProgress, 0);
                throw new InvalidOperationException("ReceiveAllAsync cannot start while ReceiveAsync is in progress.");
            }

            return new WebSocketAsyncEnumerable(
                token => ReceiveCoreAsync(token, fromAsyncEnumerable: true),
                () => Interlocked.Exchange(ref _receiveAllInProgress, 0),
                ct);
        }

        private async ValueTask<WebSocketMessage> ReceiveCoreAsync(CancellationToken ct, bool fromAsyncEnumerable)
        {
            ThrowIfDisposed();

            if (!fromAsyncEnumerable && Volatile.Read(ref _receiveAllInProgress) != 0)
                throw new InvalidOperationException("ReceiveAsync cannot run while ReceiveAllAsync is active.");

            if (Interlocked.CompareExchange(ref _receiveInProgress, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent ReceiveAsync calls are not supported.");

            try
            {
                return await _incomingQueue.DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (AsyncQueueCompletedException ex)
            {
                throw new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket connection is closed.",
                    ex);
            }
            finally
            {
                Interlocked.Exchange(ref _receiveInProgress, 0);
            }
        }

        /// <inheritdoc />
        public async Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();

            Interlocked.Exchange(ref _manualClose, 1);
            Interlocked.Exchange(ref _nextReconnectAttempt, 1);
            CancelReconnectLoop();

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                client.OnMetricsUpdated -= HandleChildMetricsUpdated;
                client.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                await client.CloseAsync(code, reason, ct).ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await StopReceivePumpAsync().ConfigureAwait(false);

            CompleteAndDrainQueue(null);
            RaiseClosed(code, reason ?? string.Empty);
        }

        /// <inheritdoc />
        public void Abort()
        {
            Interlocked.Exchange(ref _manualClose, 1);
            Interlocked.Exchange(ref _nextReconnectAttempt, 1);
            CancelReconnectLoop();

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                client.OnMetricsUpdated -= HandleChildMetricsUpdated;
                client.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                client.Abort();
                client.Dispose();
            }

            CompleteAndDrainQueue(null);
            RaiseClosed(WebSocketCloseCode.AbnormalClosure, string.Empty);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _manualClose, 1);
            Interlocked.Exchange(ref _nextReconnectAttempt, 1);
            _lifecycleCts?.Cancel();

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                client.OnMetricsUpdated -= HandleChildMetricsUpdated;
                client.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                client.Abort();
                client.Dispose();
            }

            CompleteAndDrainQueue(new ObjectDisposedException(nameof(ResilientWebSocketClient)));
            RaiseClosed(WebSocketCloseCode.AbnormalClosure, string.Empty);

            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _manualClose, 1);
            Interlocked.Exchange(ref _nextReconnectAttempt, 1);
            _lifecycleCts?.Cancel();

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                client.OnMetricsUpdated -= HandleChildMetricsUpdated;
                client.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    client.Abort();
                    client.Dispose();
                }
            }

            await StopReceivePumpAsync().ConfigureAwait(false);

            CompleteAndDrainQueue(new ObjectDisposedException(nameof(ResilientWebSocketClient)));
            RaiseClosed(WebSocketCloseCode.AbnormalClosure, string.Empty);

            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }

        private async Task ConnectReplacementClientAsync(
            bool initialConnect,
            CancellationToken externalCt,
            bool stopPreviousReceivePump = true)
        {
            var lifecycle = _lifecycleCts;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, lifecycle.Token);

            if (stopPreviousReceivePump)
                await StopReceivePumpAsync().ConfigureAwait(false);

            var nextClient = _providedTransport != null
                ? (IWebSocketClient)new WebSocketClient(_providedTransport)
                : new WebSocketClient();
            nextClient.OnMetricsUpdated += HandleChildMetricsUpdated;
            nextClient.OnConnectionQualityChanged += HandleChildConnectionQualityChanged;

            var connectOptions = _options?.Clone() ?? new WebSocketConnectionOptions();
            connectOptions.ReconnectPolicy = WebSocketReconnectPolicy.None;

            try
            {
                await nextClient.ConnectAsync(_uri, connectOptions, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                nextClient.OnMetricsUpdated -= HandleChildMetricsUpdated;
                nextClient.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                await nextClient.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // A caller's close or dispose that landed while the replacement was connecting wins: the
            // replacement is released instead of published (#1105).
            if (!initialConnect && IsClosedByCaller())
            {
                await ReleaseChildAsync(nextClient).ConfigureAwait(false);
                return;
            }

            var previous = Interlocked.Exchange(ref _client, nextClient);
            if (previous != null)
            {
                previous.OnMetricsUpdated -= HandleChildMetricsUpdated;
                previous.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            // CloseAsync, Abort and Dispose set their flag before they take _client, so a close that missed
            // the check above either took the replacement from _client itself or is seen here.
            if (!initialConnect && IsClosedByCaller())
            {
                if (ReferenceEquals(Interlocked.CompareExchange(ref _client, null, nextClient), nextClient))
                    await ReleaseChildAsync(nextClient).ConfigureAwait(false);
                return;
            }

            _latestMetrics = nextClient.Metrics;
            _latestHealth = nextClient.Health;

            _receivePumpTask = RunReceivePumpAsync(nextClient, lifecycle.Token);

            // #1116: a throwing handler does not turn the completed (re)connect into a failure.
            if (initialConnect)
                WebSocketCallbacks.Invoke(OnConnected, "[TurboHTTP] A WebSocket OnConnected handler threw.");
            else
                WebSocketCallbacks.Invoke(OnReconnected, "[TurboHTTP] A WebSocket OnReconnected handler threw.");
        }

        private async Task RunReceivePumpAsync(IWebSocketClient client, CancellationToken ct)
        {
            WebSocketException terminalError = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await client.ReceiveAsync(ct).ConfigureAwait(false);

                    // A successfully received application message marks the connection as healthy again.
                    Interlocked.Exchange(ref _nextReconnectAttempt, 1);

                    var messageHandler = OnMessage;
                    if (messageHandler == null)
                    {
                        // Pull-only consumer: lossless, the puller drives backpressure.
                        try
                        {
                            await _incomingQueue.EnqueueAsync(message, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            message.Dispose();
                            throw;
                        }

                        continue;
                    }

                    // Handlers subscribed: never wait on the pull queue, or an event-only consumer stalls
                    // this pump and the inner connection's read loop (#78). The copy is taken before the
                    // original is published to a puller that may dispose it.
                    var detachedCopy = WebSocketClient.PublishDroppingOldest(_incomingQueue, message);
                    RaiseMessage(messageHandler, detachedCopy);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected when disposing/closing.
            }
            catch (AsyncQueueCompletedException ex)
            {
                terminalError = new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket receive queue is completed.",
                    ex);
            }
            catch (WebSocketException ex)
            {
                terminalError = ex;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // Expected during disposal.
            }
            catch (Exception ex)
            {
                terminalError = new WebSocketException(
                    WebSocketError.ReceiveFailed,
                    "WebSocket receive pump failed.",
                    ex);
            }

            if (terminalError != null && !ct.IsCancellationRequested)
                await HandleUnexpectedDisconnectAsync(client, terminalError, ct).ConfigureAwait(false);
        }

        private void RaiseMessage(Action<WebSocketMessage> messageHandler, WebSocketMessage detachedCopy)
        {
            try
            {
                messageHandler(detachedCopy);
            }
            catch (Exception ex)
            {
                RaiseError(new WebSocketException(
                    WebSocketError.ReceiveFailed,
                    "WebSocket OnMessage callback failed.",
                    ex));
            }
        }

        private async Task HandleUnexpectedDisconnectAsync(
            IWebSocketClient disconnectedClient,
            WebSocketException terminalError,
            CancellationToken ct)
        {
            if (!TryEnterReconnectLoop(disconnectedClient, terminalError, ct))
                return;

            while (true)
            {
                PendingDisconnect next;
                bool loopCompleted = false;
                try
                {
                    await RunReconnectLoopAsync(terminalError, ct).ConfigureAwait(false);
                    loopCompleted = true;
                }
                finally
                {
                    // No release of disconnectedClient here: whoever takes a child out of _client owns and
                    // disposes it (this loop before it connects a replacement, CloseAsync, Abort, Dispose).
                    // Releasing it here raced a CloseAsync that had just taken it and was still closing it.
                    next = ExitReconnectLoop(handOff: loopCompleted);
                }

                if (next == null)
                    return;

                // The replacement this loop connected dropped before the loop finished; its own pump could
                // not take the loop, so it is handled here, counting as the next attempt.
                disconnectedClient = next.Client;
                terminalError = next.Error;
                ct = next.Token;
            }
        }

        /// <summary>
        /// Takes ownership of the reconnect loop. When another disconnect already owns it, a disconnect of the
        /// current connection is recorded for that owner instead of being dropped; a disconnect of a connection
        /// the loop already replaced is stale and ignored.
        /// </summary>
        private bool TryEnterReconnectLoop(IWebSocketClient client, WebSocketException error, CancellationToken ct)
        {
            lock (_sync)
            {
                if (_reconnectLoopActive == 0)
                {
                    Volatile.Write(ref _reconnectLoopActive, 1);
                    return true;
                }

                if (ReferenceEquals(client, Volatile.Read(ref _client)))
                    _pendingDisconnect = new PendingDisconnect(client, error, ct);

                return false;
            }
        }

        /// <summary>
        /// Releases the reconnect loop, or keeps it and returns the recorded disconnect when that is still
        /// the current connection's and the loop finished normally, so no disconnect is lost between the
        /// replacement's pump starting and the loop ending.
        /// </summary>
        private PendingDisconnect ExitReconnectLoop(bool handOff)
        {
            lock (_sync)
            {
                var pending = _pendingDisconnect;
                _pendingDisconnect = null;

                if (handOff &&
                    pending != null &&
                    !pending.Token.IsCancellationRequested &&
                    ReferenceEquals(pending.Client, Volatile.Read(ref _client)))
                {
                    return pending;
                }

                Volatile.Write(ref _reconnectLoopActive, 0);
                return null;
            }
        }

        private async Task RunReconnectLoopAsync(WebSocketException terminalError, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _manualClose) != 0)
            {
                CompleteAndDrainQueue(terminalError);
                RaiseClosed(
                    terminalError.CloseCode ?? WebSocketCloseCode.AbnormalClosure,
                    terminalError.CloseReason ?? string.Empty);
                return;
            }

            RaiseError(terminalError);

            WebSocketReconnectPolicy policy = _options?.ReconnectPolicy ?? WebSocketReconnectPolicy.None;
            int attempt = Volatile.Read(ref _nextReconnectAttempt);
            if (attempt < 1)
                attempt = 1;

            using (var reconnectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Published before the flags are re-read: CloseAsync and Abort set their flag before they read
                // this field, so a close either cancels this token or is seen by the checks below (#1105).
                Interlocked.Exchange(ref _reconnectCts, reconnectCts);
                var token = reconnectCts.Token;
                try
                {
                    while (!token.IsCancellationRequested && policy.ShouldReconnect(attempt, terminalError.CloseCode))
                    {
                        if (IsClosedByCaller())
                            return;

                        TimeSpan delay = policy.ComputeDelay(attempt);
                        // #1116: a throwing handler must not end the loop (queue open, no OnClosed).
                        WebSocketCallbacks.Invoke(
                            OnReconnecting,
                            attempt,
                            delay,
                            "[TurboHTTP] A WebSocket OnReconnecting handler threw.");

                        try
                        {
                            await Task.Delay(delay, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            // CloseAsync, Abort or Dispose: the closer completes the queue and raises OnClosed.
                            return;
                        }

                        if (IsClosedByCaller())
                            return;

                        var current = Interlocked.Exchange(ref _client, null);
                        if (current != null)
                        {
                            current.OnMetricsUpdated -= HandleChildMetricsUpdated;
                            current.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
                            await current.DisposeAsync().ConfigureAwait(false);
                        }

                        try
                        {
                            await ConnectReplacementClientAsync(
                                initialConnect: false,
                                externalCt: token,
                                stopPreviousReceivePump: false).ConfigureAwait(false);
                            if (!IsClosedByCaller())
                                Interlocked.Exchange(ref _nextReconnectAttempt, attempt + 1);
                            return;
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (Exception ex)
                        {
                            terminalError = ex as WebSocketException ??
                                new WebSocketException(WebSocketError.ConnectionClosed, ex.Message, ex);
                            RaiseError(terminalError);
                            attempt++;
                            Interlocked.Exchange(ref _nextReconnectAttempt, attempt);

                            if (IsPermanentConnectFailure(terminalError))
                                break;
                        }
                    }

                    if (token.IsCancellationRequested || IsClosedByCaller())
                        return;
                }
                finally
                {
                    Interlocked.CompareExchange(ref _reconnectCts, null, reconnectCts);
                }
            }

            // The connection ended on its own: messages already queued stay readable ahead of the
            // ConnectionClosed error (#1106, as WebSocketClient does since #217). Explicit CloseAsync, Abort
            // and Dispose drain whatever is left.
            _incomingQueue?.Complete(terminalError);
            RaiseClosed(
                terminalError.CloseCode ?? WebSocketCloseCode.AbnormalClosure,
                terminalError.CloseReason ?? string.Empty);
        }

        private async Task ReleaseChildAsync(IWebSocketClient child)
        {
            child.OnMetricsUpdated -= HandleChildMetricsUpdated;
            child.OnConnectionQualityChanged -= HandleChildConnectionQualityChanged;
            try
            {
                await child.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                child.Abort();
                child.Dispose();
            }
        }

        /// <summary>
        /// A 4xx rejection of the opening handshake, other than 408 Request Timeout and 429 Too Many
        /// Requests, a 407 from the proxy (RFC 9110 section 15.5, #83), a rejected server certificate, an
        /// invalid request and a cleartext policy refusal (#1139) repeat on every attempt: reconnecting
        /// would only replay the failure. The rule lives beside <see cref="WebSocketException.IsRetryable"/>.
        /// </summary>
        internal static bool IsPermanentConnectFailure(WebSocketException error)
        {
            return error != null && error.IsPermanentConnectFailure();
        }

        private Exception CreateReplacedClientException(ObjectDisposedException ex)
        {
            // R5-N2 (#84): a reconnect disposed the child client while this send was using it.
            ThrowIfDisposed();
            return new WebSocketException(
                WebSocketError.ConnectionClosed,
                "The WebSocket connection was closed or replaced during the send.",
                ex);
        }

        private IWebSocketClient RequireOpenClient()
        {
            var client = Volatile.Read(ref _client);
            if (client == null || client.State != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    "Client must be in Open state to send. Current state: " + State + ".");
            }

            return client;
        }

        private async Task StopReceivePumpAsync()
        {
            var task = _receivePumpTask;
            if (task == null)
                return;

            _receivePumpTask = null;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // Pump faults are surfaced through OnError/OnClosed.
            }
        }

        private void CompleteAndDrainQueue(Exception error)
        {
            CompleteAndDrainQueue(_incomingQueue, error);
        }

        private static void CompleteAndDrainQueue(BoundedAsyncQueue<WebSocketMessage> queue, Exception error)
        {
            if (queue == null)
                return;

            queue.Complete(error);
            queue.Drain(static message => message?.Dispose());
        }

        private bool IsClosedByCaller()
        {
            return Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _manualClose) != 0;
        }

        /// <summary>
        /// Stops a running reconnect loop's backoff delay and replacement connect. Called after the manual-close
        /// flag is set; the loop re-reads the flag after publishing its token, so neither side misses the other.
        /// </summary>
        private void CancelReconnectLoop()
        {
            var cts = Volatile.Read(ref _reconnectCts);
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The loop finished this run and disposed its token; it re-reads the flags before reconnecting.
            }
        }

        private void RaiseError(WebSocketException error)
        {
            WebSocketCallbacks.Invoke(OnError, error, "[TurboHTTP] A WebSocket OnError handler threw.");
        }

        private void HandleChildMetricsUpdated(WebSocketMetrics metrics)
        {
            _latestMetrics = metrics;
            _latestHealth = Health;

            WebSocketCallbacks.Invoke(OnMetricsUpdated, metrics, "[TurboHTTP] A WebSocket OnMetricsUpdated handler threw.");
        }

        private void HandleChildConnectionQualityChanged(ConnectionQuality quality)
        {
            _latestHealth = Health;

            WebSocketCallbacks.Invoke(
                OnConnectionQualityChanged,
                quality,
                "[TurboHTTP] A WebSocket OnConnectionQualityChanged handler threw.");
        }

        private void RaiseClosed(WebSocketCloseCode code, string reason)
        {
            if (Interlocked.CompareExchange(ref _closedEventRaised, 1, 0) != 0)
                return;

            WebSocketCallbacks.Invoke(OnClosed, code, reason ?? string.Empty, "[TurboHTTP] A WebSocket OnClosed handler threw.");
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ResilientWebSocketClient));
        }

        private sealed class PendingDisconnect
        {
            public PendingDisconnect(IWebSocketClient client, WebSocketException error, CancellationToken token)
            {
                Client = client;
                Error = error;
                Token = token;
            }

            public IWebSocketClient Client { get; }

            public WebSocketException Error { get; }

            public CancellationToken Token { get; }
        }
    }
}
