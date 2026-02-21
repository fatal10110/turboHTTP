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
        Task<Stream> ConnectAsync(
            Uri uri,
            WebSocketConnectionOptions options,
            CancellationToken ct);
    }
}
