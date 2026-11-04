#if TURBOHTTP_UNITASK
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Convenience adapters from TurboHTTP WebSocket Task/ValueTask APIs to UniTask. They live in the
    /// <c>TurboHTTP.UniTask.WebSocket</c> assembly (reference it next to <c>TurboHTTP.UniTask</c>) so that
    /// HTTP-only UniTask users do not compile <c>TurboHTTP.WebSocket</c>.
    /// </summary>
    public static class WebSocketUniTaskExtensions
    {
        // Inside namespace TurboHTTP.UniTask the bare name UniTask binds to the namespace, so the
        // non-generic UniTask type is written fully qualified.
        public static Cysharp.Threading.Tasks.UniTask ConnectAsUniTask(
            this IWebSocketClient client,
            Uri uri,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.ConnectAsync(uri, cancellationToken).AsUniTask();
        }

        public static Cysharp.Threading.Tasks.UniTask ConnectAsUniTask(
            this IWebSocketClient client,
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (options == null) throw new ArgumentNullException(nameof(options));
            return client.ConnectAsync(uri, options, cancellationToken).AsUniTask();
        }

        public static Cysharp.Threading.Tasks.UniTask SendAsUniTask(
            this IWebSocketClient client,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.SendAsync(message, cancellationToken).AsUniTask();
        }

        public static Cysharp.Threading.Tasks.UniTask SendAsUniTask(
            this IWebSocketClient client,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            return client.SendAsync(data, cancellationToken).AsUniTask();
        }

        public static Cysharp.Threading.Tasks.UniTask SendAsUniTask(
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
            return AwaitValueTask(client.ReceiveAsync(cancellationToken));
        }

        public static Cysharp.Threading.Tasks.UniTask CloseAsUniTask(
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

        // UniTask converts ValueTask only on Unity 2022.3+ (UniTask.AsValueTask.cs); await it here instead.
        private static async UniTask<T> AwaitValueTask<T>(ValueTask<T> operation)
        {
            return await operation;
        }

        private static async Cysharp.Threading.Tasks.UniTask AwaitValueTask(ValueTask operation)
        {
            await operation;
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
                return AwaitValueTask(_inner.MoveNextAsync());
            }

            public Cysharp.Threading.Tasks.UniTask DisposeAsync()
            {
                return AwaitValueTask(_inner.DisposeAsync());
            }
        }
    }
}
#endif
