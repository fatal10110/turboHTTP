using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Performance
{
    /// <summary>
    /// Middleware that enforces per-host and global concurrency limits on HTTP requests.
    /// Wraps <see cref="ConcurrencyLimiter"/> for use in the middleware pipeline.
    /// </summary>
    public sealed class ConcurrencyMiddleware : IHttpMiddleware
    {
        private readonly ConcurrencyLimiter _limiter;

        /// <summary>
        /// Creates a new concurrency middleware using the specified limiter.
        /// </summary>
        /// <param name="limiter">The concurrency limiter to enforce limits with.</param>
        /// <exception cref="ArgumentNullException"><paramref name="limiter"/> is null.</exception>
        public ConcurrencyMiddleware(ConcurrencyLimiter limiter)
        {
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        }

        /// <summary>
        /// Creates a new concurrency middleware with the specified limits.
        /// </summary>
        /// <param name="maxConnectionsPerHost">Maximum concurrent connections per host. Default 6.</param>
        /// <param name="maxTotalConnections">Maximum total concurrent connections. Default 64.</param>
        public ConcurrencyMiddleware(int maxConnectionsPerHost = 6, int maxTotalConnections = 64)
        {
            _limiter = new ConcurrencyLimiter(maxConnectionsPerHost, maxTotalConnections);
        }

        /// <inheritdoc />
        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var host = ExtractHost(request.Uri);
            context.RecordEvent("ConcurrencyAcquire");

            await _limiter.AcquireAsync(host, cancellationToken).ConfigureAwait(false);
            try
            {
                context.RecordEvent("ConcurrencyAcquired");
                return await next(request, context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _limiter.Release(host);
                context.RecordEvent("ConcurrencyReleased");
            }
        }

        private static string ExtractHost(Uri uri)
        {
            return uri?.Host ?? "unknown";
        }
    }
}
