using System;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Interceptor that collects HTTP request/response metrics.
    /// Thread-safe for concurrent use. Metrics are exposed via the Metrics property.
    /// </summary>
    public sealed class MetricsInterceptor : IHttpInterceptor
    {
        private static readonly Func<string, long, long> IncrementHostCount = static (_, count) => count + 1;
        private static readonly Func<int, long, long> IncrementStatusCodeCount = static (_, count) => count + 1;

        private readonly HttpMetrics _metrics = new HttpMetrics();
        private long _totalResponseTimeMs;
        private long _completedRequests;

        /// <summary>
        /// Access collected metrics. Read access is eventually consistent.
        /// </summary>
        public HttpMetrics Metrics => _metrics;

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return (request, handler, context, cancellationToken) =>
            {
                Interlocked.Increment(ref _metrics.TotalRequests);
                _metrics.RequestsByHost.AddOrUpdate(request.Uri.Host, 1, IncrementHostCount);
                if (!request.Body.IsEmpty)
                {
                    Interlocked.Add(ref _metrics.TotalBytesSent, request.Body.Length);
                }

                return next(
                    request,
                    new MetricsHandler(
                        handler,
                        this,
                        _metrics,
                        IncrementStatusCodeCount),
                    context,
                    cancellationToken);
            };
        }

        internal void RecordCompletion(TimeSpan elapsed)
        {
            var elapsedMs = (long)elapsed.TotalMilliseconds;
            var totalResponseTime = Interlocked.Add(ref _totalResponseTimeMs, elapsedMs);
            var completedCount = Interlocked.Increment(ref _completedRequests);
            _metrics.SetAverageResponseTimeMs((double)totalResponseTime / completedCount);
        }

        /// <summary>
        /// Reset all metrics to zero. NOT thread-safe — call only when no requests are in flight.
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _metrics.TotalRequests, 0);
            Interlocked.Exchange(ref _metrics.SuccessfulRequests, 0);
            Interlocked.Exchange(ref _metrics.FailedRequests, 0);
            Interlocked.Exchange(ref _totalResponseTimeMs, 0);
            Interlocked.Exchange(ref _completedRequests, 0);
            _metrics.SetAverageResponseTimeMs(0);
            Interlocked.Exchange(ref _metrics.TotalBytesReceived, 0);
            Interlocked.Exchange(ref _metrics.TotalBytesSent, 0);
            _metrics.RequestsByHost.Clear();
            _metrics.RequestsByStatusCode.Clear();
        }
    }
}
