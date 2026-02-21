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
                _listenerCts?.Dispose();
                _listenerCts = null;

                _options = options;
                _messageQueue = new BoundedAsyncQueue<WebSocketMessage>(options.ReceiveQueueCapacity);
                _listenerCts = new CancellationTokenSource();
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

            try
            {
                await connection.ConnectAsync(uri, transport, options, ct).ConfigureAwait(false);
                _listenerTask = RunReceiveListenerAsync(connection, _listenerCts.Token);
            }
            catch
            {
                await CleanupAfterFailedConnectAsync(connection).ConfigureAwait(false);
                throw;
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
                () => State,
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
                return await _messageQueue.DequeueAsync(ct).ConfigureAwait(false);
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

            _listenerCts?.Cancel();

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

            _listenerCts?.Cancel();

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

        private async Task RunReceiveListenerAsync(WebSocketConnection connection, CancellationToken ct)
        {
            Exception completionError = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    WebSocketMessage message = await connection.ReceiveAsync(ct).ConfigureAwait(false);

                    try
                    {
                        await _messageQueue.EnqueueAsync(message, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        message.Dispose();
                        throw;
                    }

                    var messageHandler = OnMessage;
                    if (messageHandler != null)
                    {
                        try
                        {
                            messageHandler(WebSocketMessage.CreateDetachedCopy(message));
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
                _messageQueue?.Complete(completionError);
            }
        }

        private void HandleConnectionStateChanged(WebSocketConnection connection, WebSocketState state)
        {
            if (state != WebSocketState.Closed)
                return;

            _messageQueue?.Complete(connection.TerminalError);
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

        private async Task CleanupAfterFailedConnectAsync(WebSocketConnection failedConnection)
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

            if (ReferenceEquals(_connection, failedConnection))
                _connection = null;

            DisposeTransportIfOwned();
            CompleteAndDrainQueue(null);
            _listenerCts?.Dispose();
            _listenerCts = null;
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
            var queue = _messageQueue;
            if (queue == null)
                return;

            queue.Complete(error);
            queue.Drain(static message => message?.Dispose());
        }

        private async Task StopListenerAsync()
        {
            _listenerCts?.Cancel();

            var listenerTask = _listenerTask;
            if (listenerTask == null)
                return;

            _listenerTask = null;

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
