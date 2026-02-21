using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// IAsyncEnumerable adapter over WebSocket receive calls.
    /// </summary>
    internal sealed class WebSocketAsyncEnumerable : IAsyncEnumerable<WebSocketMessage>
    {
        private readonly Func<CancellationToken, ValueTask<WebSocketMessage>> _receiveAsync;
        private readonly Func<WebSocketState> _getState;
        private readonly Action _onDispose;
        private readonly CancellationToken _baseCancellationToken;

        public WebSocketAsyncEnumerable(
            Func<CancellationToken, ValueTask<WebSocketMessage>> receiveAsync,
            Func<WebSocketState> getState,
            Action onDispose,
            CancellationToken baseCancellationToken)
        {
            _receiveAsync = receiveAsync ?? throw new ArgumentNullException(nameof(receiveAsync));
            _getState = getState ?? throw new ArgumentNullException(nameof(getState));
            _onDispose = onDispose ?? throw new ArgumentNullException(nameof(onDispose));
            _baseCancellationToken = baseCancellationToken;
        }

        public IAsyncEnumerator<WebSocketMessage> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            CancellationToken effectiveToken;
            CancellationTokenSource linkedCts = null;

            if (_baseCancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_baseCancellationToken, cancellationToken);
                effectiveToken = linkedCts.Token;
            }
            else if (_baseCancellationToken.CanBeCanceled)
            {
                effectiveToken = _baseCancellationToken;
            }
            else
            {
                effectiveToken = cancellationToken;
            }

            return new Enumerator(_receiveAsync, _getState, _onDispose, effectiveToken, linkedCts);
        }

        private sealed class Enumerator : IAsyncEnumerator<WebSocketMessage>
        {
            private readonly Func<CancellationToken, ValueTask<WebSocketMessage>> _receiveAsync;
            private readonly Func<WebSocketState> _getState;
            private readonly Action _onDispose;
            private readonly CancellationToken _cancellationToken;
            private readonly CancellationTokenSource _linkedCts;

            private int _disposed;

            public Enumerator(
                Func<CancellationToken, ValueTask<WebSocketMessage>> receiveAsync,
                Func<WebSocketState> getState,
                Action onDispose,
                CancellationToken cancellationToken,
                CancellationTokenSource linkedCts)
            {
                _receiveAsync = receiveAsync;
                _getState = getState;
                _onDispose = onDispose;
                _cancellationToken = cancellationToken;
                _linkedCts = linkedCts;
            }

            public WebSocketMessage Current { get; private set; }

            public async ValueTask<bool> MoveNextAsync()
            {
                ThrowIfDisposed();

                if (_getState() == WebSocketState.Closed)
                    return false;

                WebSocketMessage message;
                try
                {
                    message = await _receiveAsync(_cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException ex) when (ex.Error == WebSocketError.ConnectionClosed)
                {
                    return false;
                }

                Current = message;
                return true;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                    return default;

                _linkedCts?.Dispose();
                _onDispose();
                return default;
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(Enumerator));
            }
        }
    }
}
