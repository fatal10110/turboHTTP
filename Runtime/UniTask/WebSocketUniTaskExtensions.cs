#if TURBOHTTP_UNITASK
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Convenience adapters from TurboHTTP WebSocket Task/ValueTask APIs to UniTask.
    /// </summary>
    public static class WebSocketUniTaskExtensions
    {
        public static UniTask ConnectAsUniTask(
            this IWebSocketClient client,
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.ConnectAsync(uri, cancellationToken).AsUniTask();
        }

        public static UniTask ConnectAsUniTask(
            this IWebSocketClient client,
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return client.ConnectAsync(uri, options, cancellationToken).AsUniTask();
        }

        public static UniTask SendAsUniTask(
            this IWebSocketClient client,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.SendAsync(message, cancellationToken).AsUniTask();
        }

        public static UniTask SendAsUniTask(
            this IWebSocketClient client,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.SendAsync(data, cancellationToken).AsUniTask();
        }

        public static UniTask SendAsUniTask(
            this IWebSocketClient client,
            byte[] data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.SendAsync(data, cancellationToken).AsUniTask();
        }

        public static UniTask<WebSocketMessage> ReceiveAsUniTask(
            this IWebSocketClient client,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.ReceiveAsync(cancellationToken).AsUniTask();
        }

        public static UniTask CloseAsUniTask(
            this IWebSocketClient client,
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.CloseAsync(code, reason, cancellationToken).AsUniTask();
        }

        public static IUniTaskAsyncEnumerable<WebSocketMessage> ReceiveAllAsUniTaskEnumerable(
            this IWebSocketClient client,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return new UniTaskAsyncEnumerableAdapter<WebSocketMessage>(client.ReceiveAllAsync(cancellationToken));
        }

        private sealed class UniTaskAsyncEnumerableAdapter<T> : IUniTaskAsyncEnumerable<T>
        {
            private readonly IAsyncEnumerable<T> _source;

            public UniTaskAsyncEnumerableAdapter(IAsyncEnumerable<T> source)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
            }

            public IUniTaskAsyncEnumerator<T> GetAsyncEnumerator(
                CancellationToken cancellationToken = default)
            {
                return new UniTaskAsyncEnumeratorAdapter<T>(_source.GetAsyncEnumerator(cancellationToken));
            }
        }

        private sealed class UniTaskAsyncEnumeratorAdapter<T> : IUniTaskAsyncEnumerator<T>
        {
            private readonly IAsyncEnumerator<T> _inner;

            public UniTaskAsyncEnumeratorAdapter(IAsyncEnumerator<T> inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            public T Current => _inner.Current;

            public UniTask<bool> MoveNextAsync()
            {
                return _inner.MoveNextAsync().AsUniTask();
            }

            public UniTask DisposeAsync()
            {
                return _inner.DisposeAsync().AsUniTask();
            }
        }
    }
}
#endif
