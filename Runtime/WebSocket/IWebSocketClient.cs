using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Public WebSocket client surface.
    /// </summary>
    public interface IWebSocketClient : IDisposable, IAsyncDisposable
    {
        /// <summary> Event raised when the WebSocket connection is successfully established. </summary>
        event Action OnConnected;

        /// <summary> Event raised when a complete message is received. </summary>
        event Action<WebSocketMessage> OnMessage;

        /// <summary> Event raised when an error occurs during connection or message processing. </summary>
        event Action<WebSocketException> OnError;

        /// <summary> Event raised when the connection is closed. Provides the close code and reason. </summary>
        event Action<WebSocketCloseCode, string> OnClosed;

        /// <summary> Event raised when connection metrics change. </summary>
        event Action<WebSocketMetrics> OnMetricsUpdated;

        /// <summary> Event raised when the physical connection quality changes (e.g. Good to Poor). </summary>
        event Action<ConnectionQuality> OnConnectionQualityChanged;

        /// <summary> Gets the current state of the WebSocket connection. </summary>
        WebSocketState State { get; }

        /// <summary> Gets the negotiated subprotocol, or null if none. </summary>
        string SubProtocol { get; }

        /// <summary> Gets the current connection metrics. </summary>
        WebSocketMetrics Metrics { get; }

        /// <summary> Gets a snapshot of the current connection health. </summary>
        WebSocketHealthSnapshot Health { get; }

        /// <summary> Connects to the specified URI with default options. </summary>
        Task ConnectAsync(Uri uri, CancellationToken ct = default);

        /// <summary> Connects to the specified URI with custom options. </summary>
        Task ConnectAsync(Uri uri, WebSocketConnectionOptions options, CancellationToken ct = default);

        /// <summary> Sends a text message. </summary>
        Task SendAsync(string message, CancellationToken ct = default);

        /// <summary> Sends a binary message. </summary>
        Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

        /// <summary> Sends a binary message. </summary>
        Task SendAsync(byte[] data, CancellationToken ct = default);

        /// <summary> Receives the next message asynchronously. Throws if the connection closes. </summary>
        ValueTask<WebSocketMessage> ReceiveAsync(CancellationToken ct = default);

        /// <summary> Returns an asynchronous enumerable that yields messages as they arrive. </summary>
        IAsyncEnumerable<WebSocketMessage> ReceiveAllAsync(CancellationToken ct = default);

        /// <summary> Initiates a clean closure handshake with the server. </summary>
        Task CloseAsync(
            WebSocketCloseCode code = WebSocketCloseCode.NormalClosure,
            string reason = null,
            CancellationToken ct = default);

        /// <summary> Forcefully aborts the connection without a proper closure handshake. </summary>
        void Abort();
    }
}
