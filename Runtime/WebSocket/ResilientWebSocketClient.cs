using System;
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
        private AsyncBoundedQueue<WebSocketMessage> _incomingQueue;

        private int _disposed;
        private int _manualClose;
        private int _receiveInProgress;
        private int _reconnectLoopActive;
        private int _closedEventRaised;

        public event Action OnConnected;
        public event Action<WebSocketMessage> OnMessage;
        public event Action<WebSocketException> OnError;
        public event Action<WebSocketCloseCode, string> OnClosed;
        public event Action<int, TimeSpan> OnReconnecting;
        public event Action OnReconnected;

        public ResilientWebSocketClient()
        {
        }

        public ResilientWebSocketClient(IWebSocketTransport transport)
        {
            _providedTransport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

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

        public string SubProtocol => Volatile.Read(ref _client)?.SubProtocol;

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

            lock (_sync)
            {
                if (_client != null && (_client.State == WebSocketState.Open || _client.State == WebSocketState.Connecting))
                {
                    throw new InvalidOperationException(
                        "Client is already connected or connecting. Current state: " + _client.State + ".");
                }

                _uri = uri;
                _options = options;
                _incomingQueue = new AsyncBoundedQueue<WebSocketMessage>(options.ReceiveQueueCapacity);
                _lifecycleCts?.Dispose();
                _lifecycleCts = new CancellationTokenSource();
                _closedEventRaised = 0;
                _manualClose = 0;
            }

            await ConnectReplacementClientAsync(initialConnect: true, ct).ConfigureAwait(false);
        }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            if (message == null)
                throw new ArgumentNullException(nameof(message));

            var client = RequireOpenClient();
            await client.SendAsync(message, ct).ConfigureAwait(false);
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var client = RequireOpenClient();
            await client.SendAsync(data, ct).ConfigureAwait(false);
        }

        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            return SendAsync(new ReadOnlyMemory<byte>(data), ct);
        }

        public async ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

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

        public async Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();

            Interlocked.Exchange(ref _manualClose, 1);

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
                await client.CloseAsync(code, reason, ct).ConfigureAwait(false);
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await StopReceivePumpAsync().ConfigureAwait(false);

            CompleteAndDrainQueue(null);
            RaiseClosed(code, reason ?? string.Empty);
        }

        public void Abort()
        {
            Interlocked.Exchange(ref _manualClose, 1);

            var client = Interlocked.Exchange(ref _client, null);
            client?.Abort();
            client?.Dispose();

            CompleteAndDrainQueue(null);
            RaiseClosed(WebSocketCloseCode.AbnormalClosure, string.Empty);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _manualClose, 1);
            _lifecycleCts?.Cancel();

            var client = Interlocked.Exchange(ref _client, null);
            client?.Abort();
            client?.Dispose();

            CompleteAndDrainQueue(new ObjectDisposedException(nameof(ResilientWebSocketClient)));
            RaiseClosed(WebSocketCloseCode.AbnormalClosure, string.Empty);

            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref _manualClose, 1);
            _lifecycleCts?.Cancel();

            var client = Interlocked.Exchange(ref _client, null);
            if (client != null)
            {
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

        private async Task ConnectReplacementClientAsync(bool initialConnect, CancellationToken externalCt)
        {
            var lifecycle = _lifecycleCts;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalCt, lifecycle.Token);

            var nextClient = _providedTransport != null
                ? (IWebSocketClient)new WebSocketClient(_providedTransport)
                : new WebSocketClient();

            var connectOptions = _options?.Clone() ?? new WebSocketConnectionOptions();
            connectOptions.ReconnectPolicy = WebSocketReconnectPolicy.None;

            try
            {
                await nextClient.ConnectAsync(_uri, connectOptions, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                await nextClient.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            var previous = Interlocked.Exchange(ref _client, nextClient);
            if (previous != null)
                await previous.DisposeAsync().ConfigureAwait(false);

            _receivePumpTask = RunReceivePumpAsync(nextClient, lifecycle.Token);

            if (initialConnect)
            {
                var connectedHandler = OnConnected;
                connectedHandler?.Invoke();
            }
            else
            {
                var reconnectedHandler = OnReconnected;
                reconnectedHandler?.Invoke();
            }
        }

        private async Task RunReceivePumpAsync(IWebSocketClient client, CancellationToken ct)
        {
            WebSocketException terminalError = null;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var message = await client.ReceiveAsync(ct).ConfigureAwait(false);

                    try
                    {
                        await _incomingQueue.EnqueueAsync(message, ct).ConfigureAwait(false);
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
                            RaiseError(new WebSocketException(
                                WebSocketError.ReceiveFailed,
                                "WebSocket OnMessage callback failed.",
                                ex));
                        }
                    }
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

        private async Task HandleUnexpectedDisconnectAsync(
            IWebSocketClient disconnectedClient,
            WebSocketException terminalError,
            CancellationToken ct)
        {
            if (Interlocked.CompareExchange(ref _reconnectLoopActive, 1, 0) != 0)
                return;

            try
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
                int attempt = 1;

                while (!ct.IsCancellationRequested && policy.ShouldReconnect(attempt, terminalError.CloseCode))
                {
                    TimeSpan delay = policy.ComputeDelay(attempt);
                    var reconnectingHandler = OnReconnecting;
                    reconnectingHandler?.Invoke(attempt, delay);

                    await Task.Delay(delay, ct).ConfigureAwait(false);

                    var current = Interlocked.Exchange(ref _client, null);
                    if (current != null)
                        await current.DisposeAsync().ConfigureAwait(false);

                    try
                    {
                        await ConnectReplacementClientAsync(initialConnect: false, ct).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        terminalError = ex as WebSocketException ??
                            new WebSocketException(WebSocketError.ConnectionClosed, ex.Message, ex);
                        RaiseError(terminalError);
                        attempt++;
                    }
                }

                CompleteAndDrainQueue(terminalError);
                RaiseClosed(
                    terminalError.CloseCode ?? WebSocketCloseCode.AbnormalClosure,
                    terminalError.CloseReason ?? string.Empty);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnectLoopActive, 0);

                if (!ReferenceEquals(disconnectedClient, _client))
                {
                    try
                    {
                        await disconnectedClient.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        disconnectedClient.Abort();
                        disconnectedClient.Dispose();
                    }
                }
            }
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
            var queue = _incomingQueue;
            if (queue == null)
                return;

            queue.Complete(error);
            queue.Drain(static message => message?.Dispose());
        }

        private void RaiseError(WebSocketException error)
        {
            var handler = OnError;
            handler?.Invoke(error);
        }

        private void RaiseClosed(WebSocketCloseCode code, string reason)
        {
            if (Interlocked.CompareExchange(ref _closedEventRaised, 1, 0) != 0)
                return;

            var handler = OnClosed;
            handler?.Invoke(code, reason ?? string.Empty);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ResilientWebSocketClient));
        }
    }
}
