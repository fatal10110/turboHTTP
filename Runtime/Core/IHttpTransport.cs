using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Abstraction for the underlying HTTP transport layer. The default implementation,
    /// <c>RawSocketTransport</c> (TurboHTTP.Transport), uses raw TCP sockets with HTTP/1.1 and HTTP/2.
    /// Implements IDisposable for connection pool and resource cleanup.
    /// </summary>
    /// <remarks>
    /// In TurboHTTP 1.0 this interface is implemented only by TurboHTTP itself: <c>RawSocketTransport</c>
    /// and the TurboHTTP.Testing transports (<c>MockTransport</c>, <c>MockHttpServer.CreateTransport()</c>,
    /// <c>RecordReplayTransport</c>) are the supported values of <see cref="UHttpClientOptions.Transport"/>.
    /// Implementations outside TurboHTTP are not supported in 1.0: a transport needs internal contracts
    /// (reading stream and factory request bodies, request trailers, transport behavior flags) that are
    /// not public yet, and members may be added to this interface in a minor release until custom
    /// transports are supported. Public custom transport support is planned (#1237). To observe or
    /// change requests, use an <see cref="IHttpInterceptor"/> instead.
    /// </remarks>
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
