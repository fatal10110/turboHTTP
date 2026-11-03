using System;
using System.Collections;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Unity;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Unity.WebSocket
{
    /// <summary>
    /// Coroutine returned by <see cref="UnityWebSocketBridge.ReceiveAsCoroutine"/>: pass it to
    /// <c>StartCoroutine</c>, and dispose it when you stop the coroutine yourself.
    /// </summary>
    /// <remarks>
    /// Unity never disposes a coroutine enumerator that it stops (<c>StopCoroutine</c>,
    /// <c>StopAllCoroutines</c>, host destroyed), so cleanup cannot live in an iterator <c>finally</c>
    /// block (#177). This handle owns the lifecycle binding and the pending receive instead.
    /// <see cref="Dispose"/> cancels the pending receive, releases the binding and disposes a message that
    /// arrives after the stop. Destroying the callback owner or canceling the token passed to
    /// <see cref="UnityWebSocketBridge.ReceiveAsCoroutine"/> cancels the pending receive as well.
    /// Drive it from the main thread (as Unity does); <see cref="Dispose"/> may be called from any thread.
    /// </remarks>
    public sealed class UnityWebSocketReceiveCoroutine : IEnumerator, IDisposable
    {
        private readonly IWebSocketClient _client;
        private readonly Action<WebSocketMessage> _onMessage;
        private readonly Action<Exception> _onError;
        private readonly CancellationTokenSource _stop;
        private readonly LifecycleCancellationBinding _lifecycle;
        private readonly CancellationTokenRegistration _lifecycleRegistration;

        // Main thread only (MoveNext).
        private Task _receiveTask;
        private bool _finished;

        // Hand-off between the receive continuation and MoveNext/release: whoever exchanges it out owns it.
        private WebSocketMessage _ready;
        private int _released;
        private int _disposed;

        internal UnityWebSocketReceiveCoroutine(
            IWebSocketClient client,
            Action<WebSocketMessage> onMessage,
            Action<Exception> onError,
            CancellationToken cancellationToken,
            UnityEngine.Object callbackOwner,
            bool cancelOnOwnerInactive)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _onMessage = onMessage;
            _onError = onError;
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lifecycle = LifecycleCancellation.Bind(callbackOwner, _stop.Token, cancelOnOwnerInactive);
            _lifecycleRegistration = _lifecycle.Token.Register(
                static state => ((UnityWebSocketReceiveCoroutine)state).ReleasePendingMessage(),
                this);
        }

        /// <summary>
        /// Always <see langword="null"/>: the coroutine waits one frame per step.
        /// </summary>
        public object Current => null;

        /// <summary>
        /// Whether <see cref="Dispose"/> ran, explicitly or because the coroutine ended.
        /// </summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <summary>
        /// Advances the coroutine: delivers every message already received and returns
        /// <see langword="true"/> while a receive is pending.
        /// </summary>
        public bool MoveNext()
        {
            if (_finished)
                return false;

            while (true)
            {
                if (IsDisposed || _lifecycle.IsCancellationRequested)
                    return Finish();

                if (_receiveTask == null)
                {
                    CancellationToken token;
                    try
                    {
                        token = _lifecycle.Token;
                    }
                    catch (ObjectDisposedException)
                    {
                        return Finish();
                    }

                    _receiveTask = ReceiveOneAsync(token);
                }

                if (!_receiveTask.IsCompleted)
                    return true;

                var task = _receiveTask;
                _receiveTask = null;

                if (task.IsCanceled || _lifecycle.IsCancellationRequested || IsDisposed)
                    return Finish();

                if (task.IsFaulted)
                {
                    var error = task.Exception?.GetBaseException() ?? task.Exception;
                    Finish();
                    _onError?.Invoke(error);
                    return false;
                }

                var message = Interlocked.Exchange(ref _ready, null);
                if (message == null)
                    return Finish();

                WebSocketMessage detached;
                try
                {
                    detached = WebSocketMessage.CreateDetachedCopy(message);
                }
                finally
                {
                    message.Dispose();
                }

                _onMessage?.Invoke(detached);
            }
        }

        /// <summary>
        /// Not supported.
        /// </summary>
        public void Reset()
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Ends the coroutine: cancels the pending receive, releases the lifecycle binding and disposes a
        /// message that completes after the stop. Idempotent.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                // Cancels the binding token through its external registration, which cancels the
                // pending receive and runs ReleasePendingMessage.
                _stop.Cancel();
            }
            catch (AggregateException)
            {
                // A receive cancellation callback threw; the receive is canceled regardless.
            }

            ReleasePendingMessage();
            _lifecycleRegistration.Dispose();
            _lifecycle.Dispose();
            _stop.Dispose();
        }

        private bool Finish()
        {
            _finished = true;
            Dispose();
            return false;
        }

        private async Task ReceiveOneAsync(CancellationToken ct)
        {
            var message = await _client.ReceiveAsync(ct).ConfigureAwait(false);
            if (message == null)
                return;

            Interlocked.Exchange(ref _ready, message);
            if (Volatile.Read(ref _released) != 0)
                Interlocked.Exchange(ref _ready, null)?.Dispose();
        }

        private void ReleasePendingMessage()
        {
            Interlocked.Exchange(ref _released, 1);
            Interlocked.Exchange(ref _ready, null)?.Dispose();
        }
    }
}
