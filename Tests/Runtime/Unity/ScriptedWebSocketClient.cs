using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.WebSocket;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// IWebSocketClient fake for the Unity bridge/component tests: every event is raised synchronously on the
    /// caller's thread, and connect, close and receive can be held open by the test.
    /// </summary>
    internal sealed class ScriptedWebSocketClient : IWebSocketClient
    {
        private TaskCompletionSource<bool> _connectGate;
        private TaskCompletionSource<bool> _closeGate;
        private TaskCompletionSource<WebSocketMessage> _pendingReceive;
        private int _state;

        public event Action OnConnected;
        public event Action<WebSocketMessage> OnMessage;
        public event Action<WebSocketException> OnError;
        public event Action<WebSocketCloseCode, string> OnClosed;
#pragma warning disable CS0067 // required by the interface, not raised by this fake
        public event Action<WebSocketMetrics> OnMetricsUpdated;
        public event Action<ConnectionQuality> OnConnectionQualityChanged;
#pragma warning restore CS0067

        public WebSocketState State => (WebSocketState)Volatile.Read(ref _state);
        public string SubProtocol => null;
        public WebSocketMetrics Metrics => default;
        public WebSocketHealthSnapshot Health => WebSocketHealthSnapshot.Unknown;

        public int CloseCalls;
        public int AbortCalls;
        public int DisposeCalls;
        public bool FailClose;
        public CancellationToken CloseToken;
        public CancellationToken ConnectToken;

        /// <summary>When set, a held ConnectAsync ignores its token (a handshake that completes despite a cancel).</summary>
        public bool IgnoreConnectToken;
        public CancellationToken ReceiveToken;

        public bool IsDisposed => Volatile.Read(ref DisposeCalls) > 0;

        /// <summary>Holds the next ConnectAsync until <see cref="ReleaseConnect"/>.</summary>
        public void HoldConnect() => _connectGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseConnect() => _connectGate?.TrySetResult(true);

        /// <summary>Holds the next CloseAsync (a slow closing handshake) until <see cref="ReleaseClose"/>.</summary>
        public void HoldClose() => _closeGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseClose() => _closeGate?.TrySetResult(true);

        public Task ConnectAsync(Uri uri, CancellationToken ct = default)
        {
            return ConnectAsync(uri, null, ct);
        }

        public async Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default)
        {
            SetState(WebSocketState.Connecting);
            ConnectToken = ct;
            var gate = _connectGate;
            if (gate != null)
            {
                if (IgnoreConnectToken)
                {
                    await gate.Task.ConfigureAwait(false);
                }
                else
                {
                    var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (ct.Register(() => canceled.TrySetCanceled(ct)))
                    {
                        await Task.WhenAny(gate.Task, canceled.Task).ConfigureAwait(false);
                    }
                }
            }

            if (!IgnoreConnectToken)
                ct.ThrowIfCancellationRequested();
            SetState(WebSocketState.Open);
            OnConnected?.Invoke();
        }

        public Task SendAsync(string message, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(byte[] data, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<WebSocketMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReceiveToken = ct;
            ct.Register(() => tcs.TrySetCanceled(ct));
            _pendingReceive = tcs;
            return new ValueTask<WebSocketMessage>(tcs.Task);
        }

        public bool CompleteReceive(WebSocketMessage message) => _pendingReceive != null && _pendingReceive.TrySetResult(message);

        public IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default)
        {
            throw new InvalidOperationException("Not used by this fake.");
        }

        public async Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref CloseCalls);
            CloseToken = ct;
            SetState(WebSocketState.Closing);

            var gate = _closeGate;
            if (gate != null)
            {
                var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using (ct.Register(() => canceled.TrySetCanceled(ct)))
                {
                    await Task.WhenAny(gate.Task, canceled.Task).ConfigureAwait(false);
                }

                ct.ThrowIfCancellationRequested();
            }

            if (FailClose)
                throw new WebSocketException(WebSocketError.ConnectionClosed, "close failed (test)");

            // Like WebSocketClient: OnClosed is raised before CloseAsync completes.
            SetState(WebSocketState.Closed);
            OnClosed?.Invoke(code, reason ?? string.Empty);
        }

        public void Abort()
        {
            Interlocked.Increment(ref AbortCalls);
            SetState(WebSocketState.Closed);
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCalls);
            SetState(WebSocketState.Closed);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }

        public void EmitMessage(string text) => OnMessage?.Invoke(CreateTextMessage(text));

        public void EmitError(string message) =>
            OnError?.Invoke(new WebSocketException(WebSocketError.ConnectionClosed, message));

        public void EmitClosed(WebSocketCloseCode code, string reason) => OnClosed?.Invoke(code, reason);

        public static WebSocketMessage CreateTextMessage(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? string.Empty);
            return new WebSocketMessage(
                WebSocketMessageType.Text,
                buffer: data,
                length: data.Length,
                text: text,
                returnToPool: false);
        }

        private void SetState(WebSocketState state) => Volatile.Write(ref _state, (int)state);
    }
}
