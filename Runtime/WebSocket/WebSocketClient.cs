using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Default WebSocket client implementation backed by <see cref="WebSocketConnection"/>.
    /// </summary>
    public class WebSocketClient : IWebSocketClient
    {
        private readonly object _sync = new object();
        private readonly IWebSocketTransport _providedTransport;

        private IWebSocketTransport _transport;
        private bool _ownsTransport;
        private WebSocketConnection _connection;
        private WebSocketConnectionOptions _options;
        private BoundedAsyncQueue<WebSocketMessage> _messageQueue;
        private CancellationTokenSource _listenerCts;
        private Task _listenerTask;

        private int _disposed;
        private int _receiveInProgress;
        private int _receiveAllInProgress;
        private int _closeEventRaised;

        public event Action OnConnected;
        public event Action<WebSocketMessage> OnMessage;
        public event Action<WebSocketException> OnError;
        public event Action<WebSocketCloseCode, string> OnClosed;
        public event Action<WebSocketMetrics> OnMetricsUpdated;
        public event Action<ConnectionQuality> OnConnectionQualityChanged;

        public WebSocketClient()
        {
        }

        public WebSocketClient(IWebSocketTransport transport)
        {
            _providedTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        public static IWebSocketClient Create(
            WebSocketConnectionOptions options = null,
            IWebSocketTransport transport = null)
        {
            var effective = options?.Clone() ?? new WebSocketConnectionOptions();
            effective.Validate();

            if (effective.ReconnectPolicy != null && effective.ReconnectPolicy.Enabled)
                return transport != null
                    ? new ResilientWebSocketClient(transport)
                    : new ResilientWebSocketClient();

            return new WebSocketClient(transport);
        }

        public WebSocketState State
        {
            get
            {
                var connection = Volatile.Read(ref _connection);
                return connection?.State ?? WebSocketState.None;
            }
        }

        public string SubProtocol
        {
            get
            {
                var connection = Volatile.Read(ref _connection);
                return connection?.SubProtocol;
            }
        }

        public WebSocketMetrics Metrics
        {
            get
            {
                var connection = Volatile.Read(ref _connection);
                return connection?.Metrics ?? default;
            }
        }

        public WebSocketHealthSnapshot Health
        {
            get
            {
                var connection = Volatile.Read(ref _connection);
                return connection?.Health ?? WebSocketHealthSnapshot.Unknown;
            }
        }

        public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            return ConnectAsync(uri, new WebSocketConnectionOptions(), ct);
        }

        public async Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            options = options?.Clone() ?? new WebSocketConnectionOptions();
            options.Validate();

            if (options.ReconnectPolicy != null && options.ReconnectPolicy.Enabled)
            {
                throw new InvalidOperationException(
                    "ReconnectPolicy is enabled. Use ResilientWebSocketClient for auto-reconnect behavior.");
            }

            WebSocketConnection connection;
            IWebSocketTransport transport;
            BoundedAsyncQueue<WebSocketMessage> queue;
            CancellationTokenSource listenerCts;
            CancellationToken listenerToken;
            CancellationTokenSource previousListenerCts;
            Task previousListener;

            lock (_sync)
            {
                EnsureNotConnected();

                if (_connection != null)
                {
                    _connection.StateChanged -= HandleConnectionStateChanged;
                    _connection.Error -= HandleConnectionError;
                    _connection.MetricsUpdated -= HandleConnectionMetricsUpdated;
                    _connection.ConnectionQualityChanged -= HandleConnectionQualityChanged;
                    _connection.Dispose();
                    _connection = null;
                }

                if (_ownsTransport)
                    DisposeTransportIfOwned();

                CompleteAndDrainQueue(null);

                // #208: the previous listener is canceled (not just disposed) and observed below; it
                // only ever completes the queue it was started with, never the new one.
                previousListenerCts = _listenerCts;
                previousListener = _listenerTask;
                _listenerCts = null;
                _listenerTask = null;

                _options = options;
                queue = WebSocketMessageQueue.Create(options);
                Volatile.Write(ref _messageQueue, queue);
                listenerCts = new CancellationTokenSource();
                listenerToken = listenerCts.Token;
                _listenerCts = listenerCts;
                _closeEventRaised = 0;

                connection = new WebSocketConnection();
                connection.StateChanged += HandleConnectionStateChanged;
                connection.Error += HandleConnectionError;
                connection.MetricsUpdated += HandleConnectionMetricsUpdated;
                connection.ConnectionQualityChanged += HandleConnectionQualityChanged;
                _connection = connection;

                if (_providedTransport != null)
                {
                    transport = _providedTransport;
                    _ownsTransport = false;
                }
                else
                {
                    transport = CreateDefaultTransport(options.TlsBackend);
                    _ownsTransport = true;
                }

                _transport = transport;
            }

            // Outside the lock: a canceled listener may resume inline and run user callbacks.
            CancelQuietly(previousListenerCts);

            try
            {
                await connection.ConnectAsync(uri, transport, options, ct).ConfigureAwait(false);
            }
            catch
            {
                await ObservePreviousListenerAsync(previousListener, previousListenerCts).ConfigureAwait(false);
                await CleanupAfterFailedConnectAsync(connection, queue, listenerCts).ConfigureAwait(false);
                throw;
            }

            // The previous listener is canceled and its connection disposed, so it ends promptly;
            // awaiting it keeps its last OnMessage ahead of this connection's OnConnected.
            await ObservePreviousListenerAsync(previousListener, previousListenerCts).ConfigureAwait(false);

            var listenerTask = RunReceiveListenerAsync(connection, queue, listenerToken);
            lock (_sync)
            {
                if (ReferenceEquals(_listenerCts, listenerCts))
                    _listenerTask = listenerTask;
            }

            var connectedHandler = OnConnected;
            connectedHandler?.Invoke();
        }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            EnsureOpenState();
            await Volatile.Read(ref _connection).SendTextAsync(message, ct).ConfigureAwait(false);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureOpenState();

            await Volatile.Read(ref _connection).SendBinaryAsync(data, ct).ConfigureAwait(false);
        }

        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return SendAsync(new ReadOnlyMemory<byte>(data), ct);
        }

        public async ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
        {
            return await ReceiveCoreAsync(ct, fromAsyncEnumerable: false).ConfigureAwait(false);
        }

        public IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var state = State;
            if (state != WebSocketState.Open &&
                state != WebSocketState.Closing &&
                state != WebSocketState.Closed)
            {
                throw new InvalidOperationException(
                    "ReceiveAllAsync requires Open, Closing, or Closed state. Current state: " + state + ".");
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

            var state = State;
            if (state == WebSocketState.Closed)
            {
                var connection = Volatile.Read(ref _connection);
                var closeStatus = connection?.CloseStatus;
                throw new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket connection is closed.",
                    null,
                    closeStatus?.Code,
                    closeStatus?.Reason);
            }

            if (state != WebSocketState.Open && state != WebSocketState.Closing)
            {
                throw new InvalidOperationException(
                    "ReceiveAsync requires Open or Closing state. Current state: " + state + ".");
            }

            if (!fromAsyncEnumerable && Volatile.Read(ref _receiveAllInProgress) != 0)
                throw new InvalidOperationException("ReceiveAsync cannot run while ReceiveAllAsync is active.");

            if (Interlocked.CompareExchange(ref _receiveInProgress, 1, 0) != 0)
                throw new InvalidOperationException("Concurrent ReceiveAsync calls are not supported.");

            try
            {
                return await Volatile.Read(ref _messageQueue).DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (AsyncQueueCompletedException ex)
            {
                var connection = Volatile.Read(ref _connection);
                WebSocketCloseStatus? status = connection?.CloseStatus;

                throw new WebSocketException(
                    WebSocketError.ConnectionClosed,
                    "WebSocket connection is closed.",
                    ex,
                    status?.Code,
                    status?.Reason);
            }
            finally
            {
                Interlocked.Exchange(ref _receiveInProgress, 0);
            }
        }

        public async Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var connection = Volatile.Read(ref _connection);
            if (connection == null)
                return;

            await connection.CloseAsync(code, reason, ct).ConfigureAwait(false);

            await StopListenerAsync().ConfigureAwait(false);
            RaiseClosedFromConnection(connection);
        }

        public void Abort()
        {
            var connection = Volatile.Read(ref _connection);
            connection?.Abort();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            CancelQuietly(Volatile.Read(ref _listenerCts));

            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection != null)
            {
                connection.StateChanged -= HandleConnectionStateChanged;
                connection.Error -= HandleConnectionError;
                connection.MetricsUpdated -= HandleConnectionMetricsUpdated;
                connection.ConnectionQualityChanged -= HandleConnectionQualityChanged;
                connection.Dispose();
            }

            DisposeTransportIfOwned();
            CompleteAndDrainQueue(new ObjectDisposedException(nameof(WebSocketClient)));
            _listenerCts?.Dispose();
            _listenerCts = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            CancelQuietly(Volatile.Read(ref _listenerCts));

            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection != null)
            {
                connection.StateChanged -= HandleConnectionStateChanged;
                connection.Error -= HandleConnectionError;
                connection.MetricsUpdated -= HandleConnectionMetricsUpdated;
                connection.ConnectionQualityChanged -= HandleConnectionQualityChanged;
            }

            try
            {
                if (connection != null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                await StopListenerAsync().ConfigureAwait(false);
                DisposeTransportIfOwned();
                CompleteAndDrainQueue(new ObjectDisposedException(nameof(WebSocketClient)));
                _listenerCts?.Dispose();
                _listenerCts = null;
            }
        }

        /// <summary>
        /// Pumps <paramref name="connection"/> into <paramref name="queue"/>, the queue created with it
        /// (#208): a listener outliving a reconnect never touches the next connection's queue.
        /// </summary>
        private async Task RunReceiveListenerAsync(
            WebSocketConnection connection,
            BoundedAsyncQueue<WebSocketMessage> queue,
            CancellationToken ct)
        {
            Exception completionError = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    WebSocketMessage message = await connection.ReceiveAsync(ct).ConfigureAwait(false);

                    var messageHandler = OnMessage;
                    if (messageHandler == null)
                    {
                        // Pull-only consumer: lossless, the puller drives backpressure.
                        try
                        {
                            await queue.EnqueueAsync(message, ct).ConfigureAwait(false);
                        }
                        catch
                        {
                            message.Dispose();
                            throw;
                        }

                        continue;
                    }

                    // Handlers subscribed: never wait on the pull queue, or an event-only consumer stalls
                    // this listener and, behind it, the connection read loop (#78). The copy is taken
                    // before the original is published to a puller that may dispose it.
                    var detachedCopy = PublishDroppingOldest(queue, message);
                    RaiseMessage(messageHandler, detachedCopy);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Expected during close/dispose.
            }
            catch (AsyncQueueCompletedException ex)
            {
                completionError = ex;
            }
            catch (WebSocketException ex)
            {
                completionError = ex;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                // Expected during disposal.
            }
            catch (Exception ex)
            {
                completionError = new WebSocketException(
                    WebSocketError.ReceiveFailed,
                    "WebSocket receive loop failed.",
                    ex);
            }
            finally
            {
                queue.Complete(completionError);
            }
        }

        /// <summary>
        /// Takes the detached copy for <c>OnMessage</c>, then hands the pooled original to the pull queue
        /// without waiting (drop-oldest). Every path disposes the original or the dropped message once.
        /// </summary>
        internal static WebSocketMessage PublishDroppingOldest(
            BoundedAsyncQueue<WebSocketMessage> queue,
            WebSocketMessage message)
        {
            WebSocketMessage detachedCopy;
            try
            {
                detachedCopy = WebSocketMessage.CreateDetachedCopy(message);
                queue.EnqueueDropOldest(message, DisposeDroppedMessage);
            }
            catch
            {
                message.Dispose();
                throw;
            }

            return detachedCopy;
        }

        private static readonly Action<WebSocketMessage> DisposeDroppedMessage = DisposeDropped;

        private static void DisposeDropped(WebSocketMessage dropped)
        {
            dropped?.Dispose();
        }

        private void RaiseMessage(Action<WebSocketMessage> messageHandler, WebSocketMessage detachedCopy)
        {
            try
            {
                messageHandler(detachedCopy);
            }
            catch (Exception ex)
            {
                var error = new WebSocketException(
                    WebSocketError.ReceiveFailed,
                    "WebSocket OnMessage callback failed.",
                    ex);

                var errorHandler = OnError;
                errorHandler?.Invoke(error);
            }
        }

        private void HandleConnectionStateChanged(WebSocketConnection connection, WebSocketState state)
        {
            if (state != WebSocketState.Closed)
                return;

            // #208: only the current connection completes the current queue and raises OnClosed. A
            // replaced connection's late event (already dispatched when it was unsubscribed) is stale.
            BoundedAsyncQueue<WebSocketMessage> queue;
            lock (_sync)
            {
                if (!ReferenceEquals(_connection, connection))
                    return;

                queue = _messageQueue;
            }

            queue?.Complete(connection.TerminalError);
            RaiseClosedFromConnection(connection);
        }

        private void HandleConnectionError(WebSocketConnection connection, Exception error)
        {
            var wsError = error as WebSocketException ??
                new WebSocketException(WebSocketError.ConnectionClosed, error?.Message ?? "WebSocket error.", error);

            var errorHandler = OnError;
            errorHandler?.Invoke(wsError);
        }

        private void HandleConnectionMetricsUpdated(WebSocketConnection connection, WebSocketMetrics metrics)
        {
            var handler = OnMetricsUpdated;
            handler?.Invoke(metrics);
        }

        private void HandleConnectionQualityChanged(WebSocketConnection connection, ConnectionQuality quality)
        {
            var handler = OnConnectionQualityChanged;
            handler?.Invoke(quality);
        }

        private void RaiseClosedFromConnection(WebSocketConnection connection)
        {
            if (Interlocked.CompareExchange(ref _closeEventRaised, 1, 0) != 0)
                return;

            var status = connection.CloseStatus ??
                new WebSocketCloseStatus(WebSocketCloseCode.AbnormalClosure);

            var closedHandler = OnClosed;
            closedHandler?.Invoke(status.Code, status.Reason);
        }

        private void EnsureOpenState()
        {
            var state = State;
            if (state != WebSocketState.Open)
            {
                throw new InvalidOperationException(
                    "SendAsync requires Open state. Current state: " + state + ".");
            }
        }

        private void EnsureNotConnected()
        {
            var connection = _connection;
            if (connection == null)
                return;

            var state = connection.State;
            if (state != WebSocketState.None && state != WebSocketState.Closed)
            {
                throw new InvalidOperationException(
                    "Client is already connected or connecting. Current state: " + state + ".");
            }
        }

        private async Task CleanupAfterFailedConnectAsync(
            WebSocketConnection failedConnection,
            BoundedAsyncQueue<WebSocketMessage> queue,
            CancellationTokenSource listenerCts)
        {
            if (failedConnection == null)
                return;

            failedConnection.StateChanged -= HandleConnectionStateChanged;
            failedConnection.Error -= HandleConnectionError;
            failedConnection.MetricsUpdated -= HandleConnectionMetricsUpdated;
            failedConnection.ConnectionQualityChanged -= HandleConnectionQualityChanged;

            try
            {
                await failedConnection.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort cleanup for failed connect path.
            }

            // A reconnect from OnClosed of this failed attempt may already own the fields: only this
            // attempt's own state is released then.
            bool current;
            lock (_sync)
            {
                current = ReferenceEquals(_connection, failedConnection);
                if (current)
                    _connection = null;
                if (ReferenceEquals(_listenerCts, listenerCts))
                    _listenerCts = null;
            }

            if (current)
                DisposeTransportIfOwned();

            CompleteAndDrainQueue(queue, null);
            listenerCts.Dispose();
        }

        private static async Task ObservePreviousListenerAsync(
            Task previousListener,
            CancellationTokenSource previousListenerCts)
        {
            if (previousListener != null)
            {
                try
                {
                    await previousListener.ConfigureAwait(false);
                }
                catch
                {
                    // Listener faults are surfaced via OnError / terminal state.
                }
            }

            previousListenerCts?.Dispose();
        }

        private static void CancelQuietly(CancellationTokenSource cts)
        {
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already released by a concurrent close/dispose path.
            }
        }

        private void DisposeTransportIfOwned()
        {
            if (!_ownsTransport)
                return;

            var transport = Interlocked.Exchange(ref _transport, null);
            _ownsTransport = false;
            transport?.Dispose();
        }

        private void CompleteAndDrainQueue(Exception error)
        {
            CompleteAndDrainQueue(Volatile.Read(ref _messageQueue), error);
        }

        private static void CompleteAndDrainQueue(BoundedAsyncQueue<WebSocketMessage> queue, Exception error)
        {
            if (queue == null)
                return;

            queue.Complete(error);
            queue.Drain(static message => message?.Dispose());
        }

        private async Task StopListenerAsync()
        {
            CancellationTokenSource listenerCts;
            Task listenerTask;
            lock (_sync)
            {
                listenerCts = _listenerCts;
                listenerTask = _listenerTask;
                _listenerTask = null;
            }

            CancelQuietly(listenerCts);

            if (listenerTask == null)
                return;

            try
            {
                await listenerTask.ConfigureAwait(false);
            }
            catch
            {
                // Listener faults are surfaced via OnError / terminal state.
            }
        }

        private static IWebSocketTransport CreateDefaultTransport(TlsBackend tlsBackend)
        {
            return WebSocketTransportFactory.Create(tlsBackend);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(WebSocketClient));
        }
    }
}
