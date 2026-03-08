using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Abstraction for the underlying HTTP transport layer.
    /// Default implementation uses raw TCP sockets with HTTP/1.1 and HTTP/2 support.
    /// Can be replaced for testing (mock transport) or platform-specific backends (e.g., WebGL browser fetch).
    /// Implements IDisposable for connection pool and resource cleanup.
    /// </summary>
    public interface IHttpTransport : IDisposable
    {
        /// <summary>
        /// Execute an HTTP request and drive handler callbacks as the response is produced.
        /// </summary>
        /// <param name="request">The request to execute</param>
        /// <param name="handler">The response handler</param>
        /// <param name="context">Execution context for timeline tracking</param>
        /// <param name="cancellationToken">Cancellation token</param>
        Task DispatchAsync(
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken = default);
    }
}
