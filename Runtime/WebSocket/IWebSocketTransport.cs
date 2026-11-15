using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Creates a connected stream for WebSocket use.
    /// Ownership of the returned stream transfers to the caller.
    /// </summary>
    public interface IWebSocketTransport : IDisposable
    {
        /// <summary>Opens a stream to the host of <paramref name="uri"/>, through the proxy and TLS settings in <paramref name="options"/>, ready for the WebSocket upgrade. The caller owns and disposes the returned stream.</summary>
        Task<Stream> ConnectAsync(
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken ct);
    }
}
