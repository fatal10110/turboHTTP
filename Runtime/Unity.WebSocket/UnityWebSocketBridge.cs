using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Unity;
using TurboHTTP.WebSocket;
using UnityEngine;
using Debug = UnityEngine.Debug;

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
        private long _lastDropWarningTimestamp;
        private int _dropsSinceWarning;
        private long _dropWarningIntervalTicks = DefaultDropWarningIntervalSeconds * Stopwatch.Frequency;
        private int _pendingErrorFallbacks;

        // OnError can repeat (for example once per failing reconnect attempt), so its control-lane fallback is
        // bounded; past the bound an error callback is dropped like a message. OnConnected/OnClosed are not
        // bounded: there is one of each per connection.
        private const int MaxPendingErrorFallbacks = 32;

        // A sustained flood drops on every frame while the dispatcher still drains, so the warning is
        // throttled by time, not by delivery resuming (#90).
        private const long DefaultDropWarningIntervalSeconds = 10;

        // Pre-subscription buffer (#606): a bridge returned by UHttpClient.WebSocket(...) is already connected,
        // so messages that arrive before the caller's first OnMessage subscription are held here, bounded like
        // the client's own receive queue (WebSocketConstants.DefaultReceiveQueueCapacity / MaxBytes) with
        // drop-oldest. An empty buffer always admits one message.
        internal const int MaxPendingMessages = 100;
        internal const long MaxPendingBytes = 8L * 1024 * 1024;

        private const int PendingClosed = 0;
        private const int PendingBuffering = 1;
        private const int PendingDraining = 2;

        // _onMessage, _pendingMessages and _pendingBytes are guarded by _pendingGate; _pendingState is written
        // under it and read lock-free only for the PendingClosed fast path (Closed is terminal).
        private readonly object _pendingGate = new object();
        private Queue<WebSocketMessage> _pendingMessages;
        private long _pendingBytes;
        private int _pendingState;
        private Action<WebSocketMessage> _onMessage;

        public event Action OnConnected;

        /// <summary>
        /// Raised on the Unity main thread with each received message; the handler owns the message and
        /// disposes it. A message received while no handler is subscribed is disposed, except on a bridge
        /// returned by <c>UHttpClient.WebSocket(...)</c>: that bridge is already connected when the caller gets
        /// it, so it holds the messages received before the first subscription (at most 100 messages or 8 MB,
        /// oldest dropped first with <see cref="OnMessageDropped"/>) and delivers them, in order, to the first
        /// subscriber. The hold ends at the first subscription, at the first pull receive
        /// (<see cref="ReceiveAsync"/>, <see cref="ReceiveAllAsync"/>, <see cref="ReceiveAsCoroutine"/>), whose
        /// queue already holds those messages, or at <see cref="Dispose"/>, which disposes what is held.
        /// </summary>
        public event Action<WebSocketMessage> OnMessage
        {
            add
            {
                if (value == null)
                    return;

                bool drain = false;
                lock (_pendingGate)
                {
                    _onMessage += value;
                    if (_pendingState == PendingBuffering)
                    {
                        Volatile.Write(ref _pendingState, PendingDraining);
                        drain = true;
                    }
                }

                if (drain)
                    DrainPendingMessages();
            }
            remove
            {
                lock (_pendingGate)
                {
                    _onMessage -= value;
                }
            }
        }

        public event Action<WebSocketException> OnError;
        public event Action<WebSocketCloseCode, string> OnClosed;
        public event Action<WebSocketMetrics> OnMetricsUpdated;
        public event Action<ConnectionQuality> OnConnectionQualityChanged;
        public event Action<int, TimeSpan> OnReconnecting;
        public event Action OnReconnected;

        /// <summary>
        /// Raised when main-thread dispatcher backpressure (<see cref="MainThreadBackpressurePolicy"/>) drops a
        /// message, metrics, quality or reconnect callback; a dropped message is disposed. Raised synchronously
        /// on the thread that observed the drop (often the WebSocket receive thread), so keep the handler short
        /// and thread-safe. Connected and closed callbacks are never dropped: under backpressure they move to the
        /// dispatcher's control lane. Error callbacks move there too, up to 32 pending at once.
        /// </summary>
        public event Action OnMessageDropped;

        public UnityWebSocketBridge(IWebSocketClient client)
            : this(client, bufferMessagesUntilSubscribed: false)
        {
        }

        /// <param name="client">The wrapped client.</param>
        /// <param name="bufferMessagesUntilSubscribed">Hold messages received before the first
        /// <see cref="OnMessage"/> subscription instead of disposing them (#606).</param>
        internal UnityWebSocketBridge(IWebSocketClient client, bool bufferMessagesUntilSubscribed)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _resilientClient = client as ResilientWebSocketClient;

            if (bufferMessagesUntilSubscribed)
            {
                _pendingMessages = new Queue<WebSocketMessage>();
                _pendingState = PendingBuffering;
            }

            _client.OnConnected += HandleConnected;
            _client.OnMessage += HandleMessage;
            _client.OnError += HandleError;
            _client.OnClosed += HandleClosed;
            _client.OnMetricsUpdated += HandleMetricsUpdated;
            _client.OnConnectionQualityChanged += HandleConnectionQualityChanged;

            if (_resilientClient != null)
            {
                _resilientClient.OnReconnecting += HandleReconnecting;
                _resilientClient.OnReconnected += HandleReconnected;
            }
        }

        public WebSocketState State => _client.State;

        public string SubProtocol => _client.SubProtocol;

        public WebSocketMetrics Metrics => _client.Metrics;

        public WebSocketHealthSnapshot Health => _client.Health;

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
            ReleasePendingMessages(onlyWhileBuffering: true);
            return _client.ReceiveAsync(ct);
        }

        public IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            ReleasePendingMessages(onlyWhileBuffering: true);
            return _client.ReceiveAllAsync(ct);
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

        /// <summary>
        /// Receives messages from a Unity coroutine and invokes <paramref name="onMessage"/> on the
        /// main thread with a detached copy of each message.
        /// </summary>
        /// <remarks>
        /// Unity does not dispose a coroutine's enumerator when the coroutine is stopped
        /// (<c>StopCoroutine</c>, <c>StopAllCoroutines</c>) or its host is destroyed, so the returned handle
        /// owns the lifecycle binding and the pending receive: call <see cref="UnityWebSocketReceiveCoroutine.Dispose"/>
        /// when you stop the coroutine yourself. Destroying (or, with <paramref name="cancelOnOwnerInactive"/>,
        /// deactivating) <paramref name="callbackOwner"/> or canceling <paramref name="cancellationToken"/>
        /// cancels the pending receive without a call to <c>Dispose</c>.
        /// </remarks>
        /// <param name="onMessage">Called on the main thread with a detached copy of each message.</param>
        /// <param name="onError">Called on the main thread when the receive fails; the coroutine then ends.</param>
        /// <param name="cancellationToken">Ends the coroutine and cancels the pending receive.</param>
        /// <param name="callbackOwner">Unity object whose destruction ends the coroutine.</param>
        /// <param name="cancelOnOwnerInactive">Also end the coroutine when the owner becomes inactive.</param>
        /// <returns>A coroutine enumerator for <c>StartCoroutine</c> that is also the disposable handle.</returns>
        public UnityWebSocketReceiveCoroutine ReceiveAsCoroutine(
            Action<WebSocketMessage> onMessage,
            Action<Exception> onError = null,
            CancellationToken cancellationToken = default,
            UnityEngine.Object callbackOwner = null,
            bool cancelOnOwnerInactive = false)
        {
            ThrowIfDisposed();
            ReleasePendingMessages(onlyWhileBuffering: true);
            return new UnityWebSocketReceiveCoroutine(
                _client,
                onMessage,
                onError,
                cancellationToken,
                callbackOwner,
                cancelOnOwnerInactive);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Unsubscribe();
            ReleasePendingMessages(onlyWhileBuffering: false);
            _client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            Unsubscribe();
            ReleasePendingMessages(onlyWhileBuffering: false);
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        private void Unsubscribe()
        {
            _client.OnConnected -= HandleConnected;
            _client.OnMessage -= HandleMessage;
            _client.OnError -= HandleError;
            _client.OnClosed -= HandleClosed;
            _client.OnMetricsUpdated -= HandleMetricsUpdated;
            _client.OnConnectionQualityChanged -= HandleConnectionQualityChanged;

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

            DispatchCore(new DispatchItem(this, () => handler(), "OnConnected", onDropped: null, DispatchKind.Lifecycle));
        }

        private void HandleMessage(WebSocketMessage message)
        {
            if (message == null)
                return;

            Action<WebSocketMessage> handler;
            if (Volatile.Read(ref _pendingState) == PendingClosed)
            {
                handler = Volatile.Read(ref _onMessage);
            }
            else
            {
                int evicted = 0;
                lock (_pendingGate)
                {
                    if (_pendingState != PendingClosed)
                    {
                        // Buffering, or draining on the first subscriber's thread: queue behind the held
                        // messages so delivery stays in arrival order.
                        while (_pendingMessages.Count > 0 &&
                               (_pendingMessages.Count >= MaxPendingMessages ||
                                _pendingBytes + message.Length > MaxPendingBytes))
                        {
                            var oldest = _pendingMessages.Dequeue();
                            _pendingBytes -= oldest.Length;
                            oldest.Dispose();
                            evicted++;
                        }

                        _pendingMessages.Enqueue(message);
                        _pendingBytes += message.Length;
                        message = null;
                    }

                    handler = _onMessage;
                }

                for (int i = 0; i < evicted; i++)
                    NotifyMessageDropped();

                if (message == null)
                    return;
            }

            DeliverMessage(handler, message);
        }

        /// <summary>
        /// Delivers the held messages to the first subscriber one at a time, in arrival order; messages that
        /// arrive meanwhile queue behind them (#606). Runs on the subscribing thread only (the one that moved
        /// the state to draining); the gate is never held while a handler can run.
        /// </summary>
        private void DrainPendingMessages()
        {
            while (true)
            {
                WebSocketMessage message;
                Action<WebSocketMessage> handler;
                lock (_pendingGate)
                {
                    if (_pendingState != PendingDraining)
                        return;

                    if (_pendingMessages.Count == 0)
                    {
                        _pendingMessages = null;
                        _pendingBytes = 0;
                        Volatile.Write(ref _pendingState, PendingClosed);
                        return;
                    }

                    message = _pendingMessages.Dequeue();
                    _pendingBytes -= message.Length;
                    handler = _onMessage;
                }

                DeliverMessage(handler, message);
            }
        }

        /// <summary>
        /// Ends the pre-subscription hold and disposes what it holds: on dispose (any state), or on a pull
        /// receive while no subscriber has taken the held messages (the pull queue already has them).
        /// </summary>
        private void ReleasePendingMessages(bool onlyWhileBuffering)
        {
            if (Volatile.Read(ref _pendingState) == PendingClosed)
                return;

            Queue<WebSocketMessage> released;
            lock (_pendingGate)
            {
                if (_pendingState == PendingClosed ||
                    (onlyWhileBuffering && _pendingState != PendingBuffering))
                {
                    return;
                }

                released = _pendingMessages;
                _pendingMessages = null;
                _pendingBytes = 0;
                Volatile.Write(ref _pendingState, PendingClosed);
            }

            while (released.Count > 0)
                released.Dequeue().Dispose();
        }

        /// <summary>Test seam: messages currently held for the first <see cref="OnMessage"/> subscriber.</summary>
        internal int PendingMessageCount
        {
            get
            {
                lock (_pendingGate)
                {
                    return _pendingMessages?.Count ?? 0;
                }
            }
        }

        private void DeliverMessage(Action<WebSocketMessage> handler, WebSocketMessage message)
        {
            if (handler == null)
            {
                message.Dispose();
                return;
            }

            Dispatch(
                () => handler(message),
                "OnMessage",
                onDropped: () =>
                {
                    message.Dispose();
                });
        }

        private void HandleError(WebSocketException error)
        {
            var handler = OnError;
            if (handler == null)
                return;

            DispatchCore(new DispatchItem(this, () => handler(error), "OnError", onDropped: null, DispatchKind.BoundedLifecycle));
        }

        private void HandleClosed(WebSocketCloseCode code, string reason)
        {
            var handler = OnClosed;
            if (handler == null)
                return;

            DispatchCore(new DispatchItem(this, () => handler(code, reason), "OnClosed", onDropped: null, DispatchKind.Lifecycle));
        }

        private void HandleMetricsUpdated(WebSocketMetrics metrics)
        {
            var handler = OnMetricsUpdated;
            if (handler == null)
                return;

            Dispatch(
                () => handler(metrics),
                "OnMetricsUpdated");
        }

        private void HandleConnectionQualityChanged(ConnectionQuality quality)
        {
            var handler = OnConnectionQualityChanged;
            if (handler == null)
                return;

            Dispatch(
                () => handler(quality),
                "OnConnectionQualityChanged");
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

            DispatchCore(new DispatchItem(this, action, callbackName, onDropped, DispatchKind.Droppable));
        }

        /// <summary>
        /// Dispatches a lifecycle callback (connected, closed, disconnected) to the main thread. It takes the
        /// user lane so it stays ordered after the messages already queued; when user-lane backpressure
        /// rejects or evicts it, it is re-dispatched on the control lane, which never drops (#90).
        /// The callback runs at most once.
        /// </summary>
        internal static void DispatchLifecycle(Action action, string callbackName)
        {
            if (action == null)
                return;

            DispatchCore(new DispatchItem(null, action, callbackName, onDropped: null, DispatchKind.Lifecycle));
        }

        private static void DispatchCore(DispatchItem item)
        {
            Task dispatchTask;
            try
            {
                dispatchTask = MainThreadDispatcher.ExecuteAsync(item.RunAction);
            }
            catch (Exception ex)
            {
                item.OnUserLaneFailed(ex);
                return;
            }

            Observe(dispatchTask, item);
        }

        private static void Observe(Task dispatchTask, DispatchItem item)
        {
            if (dispatchTask.IsCompleted)
            {
                ObserveDispatchResult(dispatchTask, item);
                return;
            }

            _ = dispatchTask.ContinueWith(
                static (t, state) => ObserveDispatchResult(t, (DispatchItem)state),
                item,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void ObserveDispatchResult(Task task, DispatchItem item)
        {
            if (task == null || task.Status == TaskStatus.RanToCompletion)
                return;

            if (task.IsCanceled)
            {
                item.OnUserLaneFailed(
                    new OperationCanceledException("MainThread dispatch canceled for " + item.CallbackName + "."));
                return;
            }

            item.OnUserLaneFailed(task.Exception?.GetBaseException());
        }

        private void HandleDispatchDropped(string callbackName)
        {
            NotifyMessageDropped();

            // At most one warning per interval, carrying the drop count since the previous one (#90).
            Interlocked.Increment(ref _dropsSinceWarning);
            long now = Stopwatch.GetTimestamp();
            long last = Interlocked.Read(ref _lastDropWarningTimestamp);
            if (last != 0 && now - last < Interlocked.Read(ref _dropWarningIntervalTicks))
                return;

            if (Interlocked.CompareExchange(ref _lastDropWarningTimestamp, now, last) != last)
                return;

            int dropped = Interlocked.Exchange(ref _dropsSinceWarning, 0);
            Debug.LogWarning(
                "[TurboHTTP] Dropping Unity WebSocket callbacks due to main-thread dispatcher backpressure (" +
                dropped + " dropped since the last warning, latest: " + callbackName +
                "). Further drops raise OnMessageDropped and are logged at most once per interval.");
        }

        /// <summary>
        /// Minimum time between two backpressure drop warnings (test seam).
        /// </summary>
        internal TimeSpan DropWarningInterval
        {
            get => TimeSpan.FromSeconds((double)Interlocked.Read(ref _dropWarningIntervalTicks) / Stopwatch.Frequency);
            set => Interlocked.Exchange(
                ref _dropWarningIntervalTicks,
                (long)(value.TotalSeconds * Stopwatch.Frequency));
        }

        private void NotifyMessageDropped()
        {
            var droppedHandler = OnMessageDropped;
            if (droppedHandler == null)
                return;

            try
            {
                droppedHandler();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private bool TryReserveErrorFallback()
        {
            if (Interlocked.Increment(ref _pendingErrorFallbacks) <= MaxPendingErrorFallbacks)
                return true;

            Interlocked.Decrement(ref _pendingErrorFallbacks);
            return false;
        }

        private void ReleaseErrorFallback()
        {
            Interlocked.Decrement(ref _pendingErrorFallbacks);
        }

        private static void DispatchOnControlLane(DispatchItem item)
        {
            Task controlTask;
            try
            {
                controlTask = MainThreadDispatcher.ExecuteControlAsync(item.RunAction);
            }
            catch (Exception ex)
            {
                item.OnControlLaneCompleted();
                Debug.LogException(ex);
                return;
            }

            if (controlTask.IsCompleted)
            {
                OnControlTaskCompleted(controlTask, item);
                return;
            }

            _ = controlTask.ContinueWith(
                static (t, state) => OnControlTaskCompleted(t, (DispatchItem)state),
                item,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void OnControlTaskCompleted(Task task, DispatchItem item)
        {
            item.OnControlLaneCompleted();
            if (task.IsFaulted)
                Debug.LogException(task.Exception?.GetBaseException() ?? task.Exception);
        }

        private enum DispatchKind
        {
            /// <summary>Messages, metrics, quality, reconnect: dropped under backpressure.</summary>
            Droppable = 0,

            /// <summary>Connected/closed: falls back to the control lane, never dropped.</summary>
            Lifecycle = 1,

            /// <summary>Error: falls back to the control lane up to <see cref="MaxPendingErrorFallbacks"/>.</summary>
            BoundedLifecycle = 2
        }

        /// <summary>
        /// One dispatched callback. <see cref="Started"/> tells a backpressure drop (the callback never
        /// ran) from a fault thrown by the callback itself, and keeps a lifecycle callback that is
        /// re-dispatched on the control lane from running twice.
        /// </summary>
        private sealed class DispatchItem
        {
            private readonly UnityWebSocketBridge _owner;
            private readonly Action _action;
            private readonly Action _onDropped;
            private readonly DispatchKind _kind;
            private int _started;
            private int _reservedFallback;

            public DispatchItem(
                UnityWebSocketBridge owner,
                Action action,
                string callbackName,
                Action onDropped,
                DispatchKind kind)
            {
                _owner = owner;
                _action = action;
                CallbackName = callbackName;
                _onDropped = onDropped;
                _kind = kind;
                RunAction = Run;
            }

            public string CallbackName { get; }

            public Action RunAction { get; }

            private bool Started => Volatile.Read(ref _started) != 0;

            private void Run()
            {
                if (Interlocked.Exchange(ref _started, 1) != 0)
                    return;

                _action();
            }

            public void OnControlLaneCompleted()
            {
                if (Interlocked.Exchange(ref _reservedFallback, 0) != 0)
                    _owner?.ReleaseErrorFallback();
            }

            public void OnUserLaneFailed(Exception exception)
            {
                if (exception is MainThreadDispatchRejectedException && !Started)
                {
                    if (_kind == DispatchKind.Lifecycle)
                    {
                        DispatchOnControlLane(this);
                        return;
                    }

                    if (_kind == DispatchKind.BoundedLifecycle && _owner != null && _owner.TryReserveErrorFallback())
                    {
                        Volatile.Write(ref _reservedFallback, 1);
                        DispatchOnControlLane(this);
                        return;
                    }

                    _onDropped?.Invoke();
                    _owner?.HandleDispatchDropped(CallbackName);
                    return;
                }

                _onDropped?.Invoke();

                if (exception != null)
                    Debug.LogException(exception);
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UnityWebSocketBridge));
        }
    }
}
