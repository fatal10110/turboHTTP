using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    /// <summary>
    /// Middleware that collects HTTP request/response metrics.
    /// Thread-safe for concurrent use. Metrics are exposed via the Metrics property.
    /// </summary>
    public class MetricsMiddleware : IHttpMiddleware
    {
        private readonly HttpMetrics _metrics = new HttpMetrics();
        private long _totalResponseTimeMs;
        private long _completedRequests;

        /// <summary>
        /// Access collected metrics. Read access is eventually consistent.
        /// </summary>
        public HttpMetrics Metrics => _metrics;

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _metrics.TotalRequests);

            var host = request.Uri.Host;
            _metrics.RequestsByHost.AddOrUpdate(host, 1, (_, count) => count + 1);

            if (request.Body != null)
            {
                Interlocked.Add(ref _metrics.TotalBytesSent, request.Body.Length);
            }

            UHttpResponse response = null;
            try
            {
                response = await next(request, context, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _metrics.SuccessfulRequests);
                }
                else
                {
                    Interlocked.Increment(ref _metrics.FailedRequests);
                }

                var statusCode = (int)response.StatusCode;
                _metrics.RequestsByStatusCode.AddOrUpdate(
                    statusCode, 1, (_, count) => count + 1);

                if (!response.Body.IsEmpty)
                {
                    Interlocked.Add(ref _metrics.TotalBytesReceived, response.Body.Length);
                }

                return response;
            }
            catch
            {
                Interlocked.Increment(ref _metrics.FailedRequests);
                throw;
            }
            finally
            {
                var elapsedMs = (long)context.Elapsed.TotalMilliseconds;
                var totalResponseTime = Interlocked.Add(ref _totalResponseTimeMs, elapsedMs);
                var completedCount = Interlocked.Increment(ref _completedRequests);
                _metrics.SetAverageResponseTimeMs((double)totalResponseTime / completedCount);
            }
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
