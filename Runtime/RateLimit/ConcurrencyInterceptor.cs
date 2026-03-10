using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.RateLimit
{
    /// <summary>
    /// Interceptor that enforces per-host and global concurrency limits on HTTP requests.
    /// </summary>
    public sealed class ConcurrencyInterceptor : IHttpInterceptor, IDisposable
    {
        private readonly ConcurrencyLimiter _limiter;
        private readonly bool _ownsLimiter;
        private int _disposed;

        /// <summary>
        /// Creates a new concurrency interceptor using the specified limiter.
        /// </summary>
        /// <param name="limiter">The concurrency limiter to enforce limits with.</param>
        /// <exception cref="ArgumentNullException"><paramref name="limiter"/> is null.</exception>
        public ConcurrencyInterceptor(ConcurrencyLimiter limiter)
        {
            _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
            _ownsLimiter = false;
        }

        /// <summary>
        /// Creates a new concurrency interceptor with the specified limits.
        /// </summary>
        /// <param name="maxConnectionsPerHost">Maximum concurrent connections per host. Default 6.</param>
        /// <param name="maxTotalConnections">Maximum total concurrent connections. Default 64.</param>
        public ConcurrencyInterceptor(int maxConnectionsPerHost = 6, int maxTotalConnections = 64)
        {
            _limiter = new ConcurrencyLimiter(maxConnectionsPerHost, maxTotalConnections);
            _ownsLimiter = true;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                ThrowIfDisposed();

                var host = ExtractHost(request.Uri);
                context.RecordEvent("ConcurrencyAcquire");

                await _limiter.AcquireAsync(host, cancellationToken).ConfigureAwait(false);
                try
                {
                    context.RecordEvent("ConcurrencyAcquired");
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _limiter.Release(host);
                    context.RecordEvent("ConcurrencyReleased");
                }
            };
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_ownsLimiter)
                _limiter.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ConcurrencyInterceptor));
        }

        private static string ExtractHost(Uri uri)
        {
            if (uri == null)
                throw new ArgumentNullException(nameof(uri));

            return uri.Authority;
        }
    }
}
