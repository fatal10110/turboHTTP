using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Public WebSocket client surface.
    /// </summary>
    public interface IWebSocketClient : IDisposable, IAsyncDisposable
    {
        event Action OnConnected;

        event Action<WebSocketMessage> OnMessage;

        event Action<WebSocketException> OnError;

        event Action<WebSocketCloseCode, string> OnClosed;

        WebSocketState State { get; }

        string SubProtocol { get; }

        Task ConnectAsync(Uri uri, CancellationToken ct = default);

        Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default);

        Task SendAsync(string message, CancellationToken ct = default);

        Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

        Task SendAsync(byte[] data, CancellationToken ct = default);

        ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default);

        Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default);

        void Abort();
    }
}
