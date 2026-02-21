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

        [Header("Events")]
        public UnityEvent OnConnectedEvent = new UnityEvent();
        public UnityWebSocketStringEvent OnMessageReceivedEvent = new UnityWebSocketStringEvent();
        public UnityWebSocketBytesEvent OnBinaryReceivedEvent = new UnityWebSocketBytesEvent();
        public UnityWebSocketIntEvent OnDisconnectedEvent = new UnityWebSocketIntEvent();
        public UnityWebSocketStringEvent OnErrorEvent = new UnityWebSocketStringEvent();

        private IWebSocketClient _client;
        private UnityWebSocketBridge _bridge;
        private LifecycleCancellationBinding _lifecycleBinding;
        private CancellationTokenSource _componentCts;
        private Task _connectTask;
        private int _disposed;
        private int _closing;
        private bool _pauseDisconnected;

        public bool IsConnected => _client != null && _client.State == WebSocketState.Open;

        public WebSocketState ConnectionState => _client?.State ?? WebSocketState.None;

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

            if (IsConnected)
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

            _componentCts?.Cancel();
            _lifecycleBinding?.Dispose();
            _lifecycleBinding = null;

            TryAbortAndDispose();
            CleanupClientReferences();
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!_disconnectOnPause)
                return;

            if (pauseStatus)
            {
                if (IsConnected)
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

        public async Task ConnectAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            EnsureLifecycleScope();

            if (_connectTask != null && !_connectTask.IsCompleted)
            {
                await _connectTask.ConfigureAwait(false);
                return;
            }

            _connectTask = ConnectCoreAsync(ct);
            try
            {
                await _connectTask.ConfigureAwait(false);
            }
            finally
            {
                _connectTask = null;
            }
        }

        public void Disconnect()
        {
            ObserveTask(DisconnectAsync(), "disconnect");
        }

        public async Task DisconnectAsync(CancellationToken ct = default)
        {
            if (Interlocked.CompareExchange(ref _closing, 1, 0) != 0)
                return;

            try
            {
                var client = _client;
                if (client == null)
                    return;

                using var linked = CreateLinkedToken(ct);
                await client.CloseAsync(
                    WebSocketCloseCode.GoingAway,
                    "Unity component disabled.",
                    linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogError("[TurboHTTP] WebSocket disconnect failed: " + ex.Message);
                _client?.Abort();
            }
            finally
            {
                var client = _client;
                _client = null;

                var bridge = _bridge;
                _bridge = null;

                if (bridge != null)
                    UnwireBridgeEvents(bridge);

                if (client != null)
                {
                    try
                    {
                        await client.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        client.Dispose();
                    }
                }

                bridge?.Dispose();

                Interlocked.Exchange(ref _closing, 0);
            }
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
            ThrowIfDisposed();
            if (_client == null)
                throw new InvalidOperationException("WebSocket client is not connected.");

            using var linked = CreateLinkedToken(ct);
            await _client.SendAsync(message, linked.Token).ConfigureAwait(false);
        }

        public async Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (_client == null)
                throw new InvalidOperationException("WebSocket client is not connected.");

            using var linked = CreateLinkedToken(ct);
            await _client.SendAsync(data, linked.Token).ConfigureAwait(false);
        }

        private async Task ConnectCoreAsync(CancellationToken ct)
        {
            if (_client != null && (_client.State == WebSocketState.Open || _client.State == WebSocketState.Connecting))
                return;

            if (_client != null)
            {
                try
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    _client.Dispose();
                }

                _client = null;
                if (_bridge != null)
                {
                    UnwireBridgeEvents(_bridge);
                    _bridge.Dispose();
                    _bridge = null;
                }
            }

            if (!Uri.TryCreate(_uri, UriKind.Absolute, out var targetUri))
                throw new InvalidOperationException("WebSocket URI is invalid.");

            var options = BuildConnectionOptions();
            var client = WebSocketClient.Create(options);
            var bridge = new UnityWebSocketBridge(client);

            WireBridgeEvents(bridge);
            try
            {
                using var linked = CreateLinkedToken(ct);
                await bridge.ConnectAsync(targetUri, options, linked.Token).ConfigureAwait(false);

                _client = client;
                _bridge = bridge;
            }
            catch
            {
                UnwireBridgeEvents(bridge);
                await bridge.DisposeAsync().ConfigureAwait(false);
                throw;
            }
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

            return options;
        }

        private void WireBridgeEvents(UnityWebSocketBridge bridge)
        {
            bridge.OnConnected += HandleConnected;
            bridge.OnMessage += HandleMessage;
            bridge.OnError += HandleError;
            bridge.OnClosed += HandleClosed;
        }

        private void UnwireBridgeEvents(UnityWebSocketBridge bridge)
        {
            if (bridge == null)
                return;

            bridge.OnConnected -= HandleConnected;
            bridge.OnMessage -= HandleMessage;
            bridge.OnError -= HandleError;
            bridge.OnClosed -= HandleClosed;
        }

        private void HandleConnected()
        {
            OnConnectedEvent?.Invoke();
        }

        private void HandleMessage(WebSocketMessage message)
        {
            if (message == null)
                return;

            try
            {
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
            OnErrorEvent?.Invoke(error?.Message ?? "WebSocket error.");
        }

        private void HandleClosed(WebSocketCloseCode code, string reason)
        {
            OnDisconnectedEvent?.Invoke((int)code);
        }

        private void TryAbortAndDispose()
        {
            var client = _client;
            if (client == null)
                return;

            try
            {
                client.Abort();
            }
            catch
            {
                // Best effort teardown.
            }
            finally
            {
                try
                {
                    client.Dispose();
                }
                catch
                {
                    // Best effort teardown.
                }
            }
        }

        private void CleanupClientReferences()
        {
            var bridge = _bridge;
            _bridge = null;

            if (bridge != null)
            {
                UnwireBridgeEvents(bridge);
                bridge.Dispose();
            }

            _client = null;
            _componentCts?.Dispose();
            _componentCts = null;
        }

        private CancellationTokenSource CreateLinkedToken(CancellationToken external)
        {
            EnsureLifecycleScope();
            return CancellationTokenSource.CreateLinkedTokenSource(
                external,
                _componentCts.Token,
                _lifecycleBinding.Token);
        }

        private void EnsureLifecycleScope()
        {
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

                client._client?.Abort();
                client._client?.Dispose();
                client._client = null;
                if (client._bridge != null)
                {
                    client.UnwireBridgeEvents(client._bridge);
                    client._bridge.Dispose();
                }
                client._bridge = null;
            }
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
    }
}
