using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Unity;
using TurboHTTP.WebSocket;
using UnityEngine;

namespace TurboHTTP.Unity.WebSocket
{
    /// <summary>
    /// Main-thread dispatch bridge for IWebSocketClient.
    /// </summary>
    public sealed class UnityWebSocketBridge : IWebSocketClient
    {
        private readonly IWebSocketClient _client;
        private readonly ResilientWebSocketClient _resilientClient;
        private int _disposed;

        public event Action OnConnected;
        public event Action<WebSocketMessage> OnMessage;
        public event Action<WebSocketException> OnError;
        public event Action<WebSocketCloseCode, string> OnClosed;
        public event Action<int, TimeSpan> OnReconnecting;
        public event Action OnReconnected;
        public event Action OnMessageDropped;

        public UnityWebSocketBridge(IWebSocketClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _resilientClient = client as ResilientWebSocketClient;

            _client.OnConnected += HandleConnected;
            _client.OnMessage += HandleMessage;
            _client.OnError += HandleError;
            _client.OnClosed += HandleClosed;

            if (_resilientClient != null)
            {
                _resilientClient.OnReconnecting += HandleReconnecting;
                _resilientClient.OnReconnected += HandleReconnected;
            }
        }

        public WebSocketState State => _client.State;

        public string SubProtocol => _client.SubProtocol;

        public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.ConnectAsync(uri, ct);
        }

        public Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.ConnectAsync(uri, options, ct);
        }

        public Task SendAsync(string message, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.SendAsync(message, ct);
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.SendAsync(data, ct);
        }

        public Task SendAsync(byte[] data, CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.SendAsync(data, ct);
        }

        public ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.ReceiveAsync(ct);
        }

        public Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _client.CloseAsync(code, reason, ct);
        }

        public void Abort()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _client.Abort();
        }

        public IEnumerator ReceiveAsCoroutine(
            Action<WebSocketMessage> onMessage,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
        {
            var lifecycle = LifecycleCancellation.Bind(
                callbackOwner,
                cancellationToken,
                cancelOnOwnerInactive);

            try
            {
                while (!lifecycle.IsCancellationRequested)
                {
                    var receiveTask = _client.ReceiveAsync(lifecycle.Token).AsTask();

                    while (!receiveTask.IsCompleted)
                    {
                        if (lifecycle.IsCancellationRequested)
                            yield break;

                        yield return null;
                    }

                    if (receiveTask.IsCanceled || lifecycle.IsCancellationRequested)
                        yield break;

                    if (receiveTask.IsFaulted)
                    {
                        onError?.Invoke(receiveTask.Exception?.GetBaseException() ?? receiveTask.Exception);
                        yield break;
                    }

                    var message = receiveTask.Result;
                    var detached = WebSocketMessage.CreateDetachedCopy(message);
                    message.Dispose();
                    onMessage?.Invoke(detached);
                }
            }
            finally
            {
                lifecycle.Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Unsubscribe();
            _client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Unsubscribe();
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        private void Unsubscribe()
        {
            _client.OnConnected -= HandleConnected;
            _client.OnMessage -= HandleMessage;
            _client.OnError -= HandleError;
            _client.OnClosed -= HandleClosed;

            if (_resilientClient != null)
            {
                _resilientClient.OnReconnecting -= HandleReconnecting;
                _resilientClient.OnReconnected -= HandleReconnected;
            }
        }

        private void HandleConnected()
        {
            var handler = OnConnected;
            if (handler == null)
                return;

            Dispatch(
                () => handler(),
                "OnConnected");
        }

        private void HandleMessage(WebSocketMessage message)
        {
            if (message == null)
                return;

            WebSocketMessage detachedCopy = null;
            try
            {
                detachedCopy = WebSocketMessage.CreateDetachedCopy(message);
            }
            finally
            {
                message.Dispose();
            }

            var handler = OnMessage;
            if (handler == null)
            {
                detachedCopy.Dispose();
                return;
            }

            Dispatch(
                () => handler(detachedCopy),
                "OnMessage",
                onDropped: () =>
                {
                    detachedCopy.Dispose();
                });
        }

        private void HandleError(WebSocketException error)
        {
            var handler = OnError;
            if (handler == null)
                return;

            Dispatch(
                () => handler(error),
                "OnError");
        }

        private void HandleClosed(WebSocketCloseCode code, string reason)
        {
            var handler = OnClosed;
            if (handler == null)
                return;

            Dispatch(
                () => handler(code, reason),
                "OnClosed");
        }

        private void HandleReconnecting(int attempt, TimeSpan delay)
        {
            var handler = OnReconnecting;
            if (handler == null)
                return;

            Dispatch(
                () => handler(attempt, delay),
                "OnReconnecting");
        }

        private void HandleReconnected()
        {
            var handler = OnReconnected;
            if (handler == null)
                return;

            Dispatch(
                () => handler(),
                "OnReconnected");
        }

        private void Dispatch(Action action, string callbackName, Action onDropped = null)
        {
            if (action == null)
                return;

            Task dispatchTask;
            try
            {
                dispatchTask = MainThreadDispatcher.ExecuteAsync(action);
            }
            catch (Exception ex)
            {
                HandleDispatchFailure(ex, callbackName, onDropped);
                return;
            }

            if (dispatchTask.IsCompleted)
            {
                ObserveDispatchResult(dispatchTask, callbackName, onDropped);
                return;
            }

            _ = dispatchTask.ContinueWith(
                t => ObserveDispatchResult(t, callbackName, onDropped),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void ObserveDispatchResult(Task task, string callbackName, Action onDropped)
        {
            if (task == null || task.Status == TaskStatus.RanToCompletion)
                return;

            if (task.IsCanceled)
            {
                HandleDispatchFailure(
                    new OperationCanceledException("MainThread dispatch canceled for " + callbackName + "."),
                    callbackName,
                    onDropped);
                return;
            }

            HandleDispatchFailure(task.Exception?.GetBaseException(), callbackName, onDropped);
        }

        private void HandleDispatchFailure(Exception exception, string callbackName, Action onDropped)
        {
            if (IsDroppedDispatch(exception))
            {
                onDropped?.Invoke();
                NotifyMessageDropped();
                Debug.LogWarning(
                    "[TurboHTTP] Dropped Unity WebSocket callback due to dispatcher backpressure: " +
                    callbackName + ".");
                return;
            }

            if (exception != null)
                Debug.LogException(exception);
        }

        private static bool IsDroppedDispatch(Exception exception)
        {
            if (exception == null)
                return false;

            string message = exception.Message ?? string.Empty;
            return message.IndexOf("dropped", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void NotifyMessageDropped()
        {
            var droppedHandler = OnMessageDropped;
            droppedHandler?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UnityWebSocketBridge));
        }
    }
}
