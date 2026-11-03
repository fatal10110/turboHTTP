using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Unity;
using TurboHTTP.WebSocket;
using UnityEngine;
using UnityEngine.Events;

namespace TurboHTTP.Unity.WebSocket
{
    [Serializable]
    public sealed class UnityWebSocketStringEvent : UnityEvent<string>
    {
    }

    [Serializable]
    public sealed class UnityWebSocketBytesEvent : UnityEvent<byte[]>
    {
    }

    [Serializable]
    public sealed class UnityWebSocketIntEvent : UnityEvent<int>
    {
    }

    /// <summary>
    /// MonoBehaviour component for Unity-friendly WebSocket lifecycle handling.
    /// </summary>
    public sealed class UnityWebSocketClient : MonoBehaviour
    {
        private static readonly object ActiveGate = new object();
        private static readonly List<UnityWebSocketClient> ActiveClients = new List<UnityWebSocketClient>();
        private static int _quittingHookRegistered;

        [SerializeField]
        private string _uri;

        [SerializeField]
        private bool _autoConnect;

        [SerializeField]
        private bool _autoReconnect;

        [SerializeField]
        private string _subProtocol;

        [SerializeField]
        private float _pingIntervalSeconds = 25f;

        [SerializeField]
        private bool _disconnectOnPause = true;

        [SerializeField]
        [Tooltip("Allow ws:// and http:// proxy legs on iOS/Android players, where cleartext is refused by default. " +
                 "Android also requires the app's network security config to permit the host.")]
        private bool _allowCleartext;

        [Header("Events")]
        public UnityEvent OnConnectedEvent = new UnityEvent();
        public UnityWebSocketStringEvent OnMessageReceivedEvent = new UnityWebSocketStringEvent();
        public UnityWebSocketBytesEvent OnBinaryReceivedEvent = new UnityWebSocketBytesEvent();
        public UnityWebSocketIntEvent OnDisconnectedEvent = new UnityWebSocketIntEvent();
        public UnityWebSocketStringEvent OnErrorEvent = new UnityWebSocketStringEvent();

        // Guards the connection/lifecycle fields below. Unity messages run on the main thread; connect and
        // disconnect continuations run on the thread pool (#91).
        private readonly object _stateGate = new object();
        private Connection _connection;
        private Task _connectTask;
        private int _connectGeneration;
        private Task _disconnectTask;
        private int _generation;
        private LifecycleCancellationBinding _lifecycleBinding;
        private CancellationTokenSource _componentCts;

        private int _disposed;

        // Main thread only (Unity messages).
        private bool _pauseDisconnected;

        /// <summary>
        /// Test seam: creates the client for each connect. <see langword="null"/> uses
        /// <see cref="WebSocketClient.Create(WebSocketConnectionOptions)"/>.
        /// </summary>
        internal Func<WebSocketConnectionOptions, IWebSocketClient> ClientFactory { get; set; }

        /// <summary>
        /// Test seam: the serialized URI.
        /// </summary>
        internal string ConfiguredUri
        {
            get => _uri;
            set => _uri = value;
        }

        public bool IsConnected => Volatile.Read(ref _connection)?.Client.State == WebSocketState.Open;

        public WebSocketState ConnectionState => Volatile.Read(ref _connection)?.Client.State ?? WebSocketState.None;

        /// <summary>
        /// Whether the next connect may use cleartext (<c>ws://</c> or an <c>http://</c> proxy leg)
        /// on iOS/Android players, where it is refused by default. <see langword="false"/> keeps the
        /// platform default (<see cref="TurboHTTP.Core.CleartextTrafficPolicy.DefaultAllowCleartext"/>).
        /// On Android the app's network security configuration must also permit the host.
        /// </summary>
        public bool AllowCleartext
        {
            get => _allowCleartext;
            set => _allowCleartext = value;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            lock (ActiveGate)
            {
                ActiveClients.Clear();
            }

            Interlocked.Exchange(ref _quittingHookRegistered, 0);
        }

        private void OnEnable()
        {
            lock (ActiveGate)
            {
                if (!ActiveClients.Contains(this))
                    ActiveClients.Add(this);
            }

            EnsureQuittingHook();
            EnsureLifecycleScope();

            if (_autoConnect)
                Connect();
        }

        private void OnDisable()
        {
            lock (ActiveGate)
            {
                ActiveClients.Remove(this);
            }

            // Also supersedes a connect still in flight, so a disabled component never ends up connected.
            if (IsConnected || IsConnectInFlight())
                Disconnect();
        }

        private void OnDestroy()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            lock (ActiveGate)
            {
                ActiveClients.Remove(this);
            }

            Connection connection;
            CancellationTokenSource componentCts;
            LifecycleCancellationBinding lifecycleBinding;
            lock (_stateGate)
            {
                _generation++;
                connection = _connection;
                Volatile.Write(ref _connection, null);
                componentCts = _componentCts;
                _componentCts = null;
                lifecycleBinding = _lifecycleBinding;
                _lifecycleBinding = null;
            }

            // Cancel first: an in-flight connect or graceful close (OnDisable in the previous frame) stops
            // and aborts its own client. Linked tokens are only created under _stateGate, which now sees
            // no source, so disposing the sources after the cancel cannot race a reader (#177).
            CancelQuietly(componentCts);

            if (connection != null)
            {
                connection.Unwire(this);
                AbortAndDispose(connection);
            }

            lifecycleBinding?.Dispose();
            componentCts?.Dispose();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!_disconnectOnPause)
                return;

            if (pauseStatus)
            {
                if (IsConnected || IsConnectInFlight())
                {
                    _pauseDisconnected = true;
                    Disconnect();
                }
            }
            else if (_autoReconnect && _pauseDisconnected)
            {
                _pauseDisconnected = false;
                Connect();
            }
        }

        public void Connect()
        {
            ObserveTask(ConnectAsync(), "connect");
        }

        /// <summary>
        /// Connects to the configured URI. A connect issued while a disconnect is closing the previous
        /// connection waits for that close to finish first, so the new client is never torn down by it
        /// (#91). A <see cref="Disconnect"/> (or destroy) issued while this connect is in flight cancels it.
        /// </summary>
        public Task ConnectAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();

            var completion = new TaskCompletionSource<bool>();
            Task supersededConnect = null;
            Task pendingDisconnect;
            int generation;
            lock (_stateGate)
            {
                EnsureLifecycleScope_NoLock();

                if (_connectTask != null && !_connectTask.IsCompleted)
                {
                    if (_connectGeneration == _generation)
                        return _connectTask;

                    // A disconnect superseded that connect; it tears down its own client. Start a new one
                    // after it so the two connections' events stay ordered.
                    supersededConnect = _connectTask;
                }

                pendingDisconnect = _disconnectTask;
                generation = _generation;
                _connectTask = completion.Task;
                _connectGeneration = generation;
            }

            // Published first, started outside the lock (synchronously up to its first await).
            CompleteFrom(ConnectCoreAsync(supersededConnect, pendingDisconnect, generation, ct), completion);
            return completion.Task;
        }

        public void Disconnect()
        {
            ObserveTask(DisconnectAsync(), "disconnect");
        }

        /// <summary>
        /// Closes the current connection with <see cref="WebSocketCloseCode.GoingAway"/> and cancels a
        /// connect still in flight. Concurrent calls share one close. <see cref="OnDisconnectedEvent"/> is
        /// raised exactly once per connection.
        /// </summary>
        public Task DisconnectAsync(CancellationToken ct = default)
        {
            TaskCompletionSource<bool> completion;
            Connection connection;
            lock (_stateGate)
            {
                _generation++;

                if (_disconnectTask != null && !_disconnectTask.IsCompleted)
                    return _disconnectTask;

                connection = _connection;
                if (connection == null)
                    return Task.CompletedTask;

                completion = new TaskCompletionSource<bool>();
                _disconnectTask = completion.Task;
            }

            CompleteFrom(DisconnectCoreAsync(connection, ct), completion);
            return completion.Task;
        }

        public void Send(string message)
        {
            ObserveTask(SendAsync(message), "send text");
        }

        public void Send(byte[] data)
        {
            ObserveTask(SendAsync(data), "send binary");
        }

        public async Task SendAsync(string message, CancellationToken ct = default)
        {
            var client = RequireClient();
            using var linked = CreateLinkedToken(ct);
            await client.SendAsync(message, linked.Token).ConfigureAwait(false);
        }

        public async Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            var client = RequireClient();
            using var linked = CreateLinkedToken(ct);
            await client.SendAsync(data, linked.Token).ConfigureAwait(false);
        }

        private IWebSocketClient RequireClient()
        {
            ThrowIfDisposed();
            var connection = Volatile.Read(ref _connection);
            if (connection == null)
                throw new InvalidOperationException("WebSocket client is not connected.");

            return connection.Client;
        }

        private bool IsConnectInFlight()
        {
            lock (_stateGate)
            {
                return _connectTask != null && !_connectTask.IsCompleted;
            }
        }

        private async Task ConnectCoreAsync(
            Task supersededConnect,
            Task pendingDisconnect,
            int generation,
            CancellationToken ct)
        {
            await AwaitQuietly(supersededConnect).ConfigureAwait(false);
            await AwaitQuietly(pendingDisconnect).ConfigureAwait(false);

            Connection stale = null;
            lock (_stateGate)
            {
                ThrowIfSuperseded_NoLock(generation);

                var current = _connection;
                if (current != null)
                {
                    var state = current.Client.State;
                    if (state == WebSocketState.Open || state == WebSocketState.Connecting)
                        return;

                    // Closed by the peer or failed: replace it.
                    stale = current;
                    Volatile.Write(ref _connection, null);
                }
            }

            if (stale != null)
                await ReleaseConnectionAsync(stale, WebSocketCloseCode.AbnormalClosure).ConfigureAwait(false);

            if (!Uri.TryCreate(_uri, UriKind.Absolute, out var targetUri))
                throw new InvalidOperationException("WebSocket URI is invalid.");

            var options = BuildConnectionOptions();
            var factory = ClientFactory;
            var client = factory != null ? factory(options) : WebSocketClient.Create(options);
            var connection = new Connection(client, new UnityWebSocketBridge(client), this);

            connection.Wire(this);
            try
            {
                using var linked = CreateLinkedToken(ct);
                await connection.Bridge.ConnectAsync(targetUri, options, linked.Token).ConfigureAwait(false);
            }
            catch
            {
                connection.Unwire(this);
                await connection.Bridge.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            bool published;
            lock (_stateGate)
            {
                published = generation == _generation && _connection == null && Volatile.Read(ref _disposed) == 0;
                if (published)
                    Volatile.Write(ref _connection, connection);
            }

            if (published)
                return;

            // A Disconnect or destroy ran while the handshake was in flight: close the client it never saw.
            await CloseConnectionAsync(connection, CancellationToken.None).ConfigureAwait(false);
            throw new OperationCanceledException("The WebSocket connect was superseded by a disconnect.");
        }

        private static void CompleteFrom(Task source, TaskCompletionSource<bool> target)
        {
            _ = source.ContinueWith(
                static (t, state) =>
                {
                    var tcs = (TaskCompletionSource<bool>)state;
                    if (t.IsCanceled)
                        tcs.TrySetCanceled();
                    else if (t.IsFaulted)
                        tcs.TrySetException(t.Exception.InnerExceptions);
                    else
                        tcs.TrySetResult(true);
                },
                target,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static async Task AwaitQuietly(Task task)
        {
            if (task == null)
                return;

            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // That operation reports its own failure.
            }
        }

        private async Task DisconnectCoreAsync(Connection connection, CancellationToken ct)
        {
            try
            {
                await CloseConnectionAsync(connection, ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_stateGate)
                {
                    // Only the connection this disconnect owns; a newer one is never touched (#91).
                    if (ReferenceEquals(_connection, connection))
                        Volatile.Write(ref _connection, null);
                }
            }
        }

        /// <summary>
        /// Graceful close of one connection, then release. Never throws.
        /// </summary>
        private async Task CloseConnectionAsync(Connection connection, CancellationToken ct)
        {
            var closeCode = WebSocketCloseCode.GoingAway;
            try
            {
                using var linked = CreateLinkedToken(ct);
                await connection.Client.CloseAsync(
                    WebSocketCloseCode.GoingAway,
                    "Unity component disabled.",
                    linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                closeCode = WebSocketCloseCode.AbnormalClosure;

                // OnDestroy cancels and aborts an in-flight close on purpose; that is not a failure.
                if (Volatile.Read(ref _disposed) == 0)
                    Debug.LogError("[TurboHTTP] WebSocket disconnect failed: " + ex.Message);

                try
                {
                    connection.Client.Abort();
                }
                catch
                {
                    // Best effort teardown.
                }
            }

            await ReleaseConnectionAsync(connection, closeCode).ConfigureAwait(false);
        }

        /// <summary>
        /// Unwires and disposes one connection, then raises its <see cref="OnDisconnectedEvent"/> if the
        /// client's own close event has not (an aborted or failed close raises none).
        /// </summary>
        private async Task ReleaseConnectionAsync(Connection connection, WebSocketCloseCode fallbackCode)
        {
            connection.Unwire(this);

            try
            {
                await connection.Client.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    connection.Client.Dispose();
                }
                catch
                {
                    // Best effort teardown.
                }
            }

            connection.Bridge.Dispose();

            // Queued behind the close event the client raised before CloseAsync completed, so that one
            // wins when it exists.
            if (!connection.DisconnectRaised)
                UnityWebSocketBridge.DispatchLifecycle(
                    () => RaiseDisconnected(connection, fallbackCode),
                    "OnDisconnected");
        }

        private WebSocketConnectionOptions BuildConnectionOptions()
        {
            var options = new WebSocketConnectionOptions
            {
                PingInterval = _pingIntervalSeconds <= 0f
                    ? TimeSpan.Zero
                    : TimeSpan.FromSeconds(_pingIntervalSeconds)
            };

            if (!string.IsNullOrWhiteSpace(_subProtocol))
            {
                options.SubProtocols = new[] { _subProtocol.Trim() };
            }

            if (_autoReconnect)
            {
                options.WithReconnection(WebSocketReconnectPolicy.Default);
            }

            // Unchecked keeps the platform default (CleartextTrafficPolicy.DefaultAllowCleartext).
            if (_allowCleartext)
                options.AllowCleartext = true;

            return options;
        }

        private void HandleConnected()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            OnConnectedEvent?.Invoke();
        }

        private void HandleMessage(WebSocketMessage message)
        {
            if (message == null)
                return;

            try
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;

                if (message.IsText)
                {
                    OnMessageReceivedEvent?.Invoke(message.Text ?? string.Empty);
                    return;
                }

                byte[] bytes = message.Length == 0 ? Array.Empty<byte>() : message.Data.ToArray();
                OnBinaryReceivedEvent?.Invoke(bytes);
            }
            finally
            {
                message.Dispose();
            }
        }

        private void HandleError(WebSocketException error)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            OnErrorEvent?.Invoke(error?.Message ?? "WebSocket error.");
        }

        private void RaiseDisconnected(Connection connection, WebSocketCloseCode code)
        {
            if (!connection.TryMarkDisconnected())
                return;

            if (Volatile.Read(ref _disposed) != 0)
                return;

            OnDisconnectedEvent?.Invoke((int)code);
        }

        private static void AbortAndDispose(Connection connection)
        {
            // Destroy and quit raise no events after this point.
            connection.TryMarkDisconnected();

            try
            {
                connection.Client.Abort();
            }
            catch
            {
                // Best effort teardown.
            }

            try
            {
                connection.Bridge.Dispose();
            }
            catch
            {
                // Best effort teardown.
            }
        }

        private CancellationTokenSource CreateLinkedToken(CancellationToken external)
        {
            lock (_stateGate)
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    EnsureLifecycleScope_NoLock();
                    return CancellationTokenSource.CreateLinkedTokenSource(
                        external,
                        _componentCts.Token,
                        _lifecycleBinding.Token);
                }
            }

            // Destroyed: the component's sources are gone, so hand out an already canceled one.
            var canceled = new CancellationTokenSource();
            canceled.Cancel();
            return canceled;
        }

        private void EnsureLifecycleScope()
        {
            lock (_stateGate)
            {
                EnsureLifecycleScope_NoLock();
            }
        }

        private void EnsureLifecycleScope_NoLock()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            if (_componentCts == null)
                _componentCts = new CancellationTokenSource();

            if (_lifecycleBinding == null)
            {
                _lifecycleBinding = LifecycleCancellation.Bind(
                    owner: this,
                    externalToken: _componentCts.Token,
                    cancelOnOwnerInactive: false);
            }
        }

        private void ThrowIfSuperseded_NoLock(int generation)
        {
            if (generation != _generation || Volatile.Read(ref _disposed) != 0)
                throw new OperationCanceledException("The WebSocket connect was superseded by a disconnect.");
        }

        private static void CancelQuietly(CancellationTokenSource cts)
        {
            if (cts == null)
                return;

            try
            {
                cts.Cancel();
            }
            catch (AggregateException ex)
            {
                Debug.LogException(ex);
            }
        }

        private static void EnsureQuittingHook()
        {
            if (Interlocked.CompareExchange(ref _quittingHookRegistered, 1, 0) != 0)
                return;

            Application.quitting += HandleApplicationQuitting;
        }

        private static void HandleApplicationQuitting()
        {
            UnityWebSocketClient[] snapshot;
            lock (ActiveGate)
            {
                snapshot = ActiveClients.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var client = snapshot[i];
                if (client == null)
                    continue;

                client.AbortForQuit();
            }
        }

        private void AbortForQuit()
        {
            Connection connection;
            lock (_stateGate)
            {
                _generation++;
                connection = _connection;
                Volatile.Write(ref _connection, null);
            }

            if (connection == null)
                return;

            connection.Unwire(this);
            AbortAndDispose(connection);
        }

        private void ObserveTask(Task task, string operationName)
        {
            if (task == null)
                return;

            _ = task.ContinueWith(
                t =>
                {
                    if (t.IsCanceled || t.IsCompletedSuccessfully)
                        return;

                    // Teardown after OnDestroy cancels and aborts on purpose.
                    if (Volatile.Read(ref _disposed) != 0)
                        return;

                    var ex = t.Exception?.GetBaseException() ?? t.Exception;
                    Debug.LogError("[TurboHTTP] UnityWebSocketClient " + operationName + " failed: " + ex?.Message);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UnityWebSocketClient));
        }

        /// <summary>
        /// One connect attempt's client and bridge, with the once-only disconnect flag (#91).
        /// </summary>
        private sealed class Connection
        {
            private readonly Action<WebSocketCloseCode, string> _closedHandler;
            private int _disconnectRaised;

            public Connection(IWebSocketClient client, UnityWebSocketBridge bridge, UnityWebSocketClient owner)
            {
                Client = client;
                Bridge = bridge;
                _closedHandler = (code, reason) => owner.RaiseDisconnected(this, code);
            }

            public IWebSocketClient Client { get; }

            public UnityWebSocketBridge Bridge { get; }

            public bool DisconnectRaised => Volatile.Read(ref _disconnectRaised) != 0;

            public bool TryMarkDisconnected()
            {
                return Interlocked.Exchange(ref _disconnectRaised, 1) == 0;
            }

            public void Wire(UnityWebSocketClient owner)
            {
                Bridge.OnConnected += owner.HandleConnected;
                Bridge.OnMessage += owner.HandleMessage;
                Bridge.OnError += owner.HandleError;
                Bridge.OnClosed += _closedHandler;
            }

            public void Unwire(UnityWebSocketClient owner)
            {
                Bridge.OnConnected -= owner.HandleConnected;
                Bridge.OnMessage -= owner.HandleMessage;
                Bridge.OnError -= owner.HandleError;
                Bridge.OnClosed -= _closedHandler;
            }
        }
    }
}
