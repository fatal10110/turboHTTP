using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    /// <summary>
    /// Mock transport for unit testing middleware and client behavior.
    /// Returns configurable responses without making real network calls.
    /// Thread-safe for concurrent use.
    /// </summary>
    public class MockTransport : IHttpTransport
    {
        private readonly Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> _handler;
        private int _requestCount;
        private volatile UHttpRequest _lastRequest;

        /// <summary>
        /// Number of times SendAsync has been called.
        /// </summary>
        public int RequestCount => Interlocked.CompareExchange(ref _requestCount, 0, 0);

        /// <summary>
        /// The most recent request received by this transport.
        /// </summary>
        public UHttpRequest LastRequest => _lastRequest;

        /// <summary>
        /// Create a MockTransport that returns the specified status code with
        /// optional headers, body, and error.
        /// </summary>
        public MockTransport(
            HttpStatusCode statusCode = HttpStatusCode.OK,
            HttpHeaders headers = null,
            byte[] body = null,
            UHttpError error = null)
        {
            var responseHeaders = headers ?? new HttpHeaders();
            _handler = (req, ctx, ct) =>
            {
                var response = new UHttpResponse(
                    statusCode, responseHeaders, body, ctx.Elapsed, req, error);
                return Task.FromResult(response);
            };
        }

        /// <summary>
        /// Create a MockTransport with a custom handler for advanced scenarios
        /// (e.g., fail first N requests, return different responses per request).
        /// </summary>
        public MockTransport(
            Func<UHttpRequest, RequestContext, CancellationToken, Task<UHttpResponse>> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        /// <summary>
        /// Create a MockTransport with a simplified custom handler (sync, no context).
        /// </summary>
        public MockTransport(Func<UHttpRequest, UHttpResponse> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _handler = (req, ctx, ct) => Task.FromResult(handler(req));
        }

        public Task<UHttpResponse> SendAsync(
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            _lastRequest = request;

            cancellationToken.ThrowIfCancellationRequested();

            return _handler(request, context, cancellationToken);
        }

        public void Dispose()
        {
            // No resources to dispose
        }
    }
}
