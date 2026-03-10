using System;
using System.Threading;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    internal sealed class MetricsHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly MetricsInterceptor _owner;
        private readonly HttpMetrics _metrics;
        private readonly Func<int, long, long> _incrementStatusCodeCount;

        private int _statusCode;

        internal MetricsHandler(
            IHttpHandler inner,
            MetricsInterceptor owner,
            HttpMetrics metrics,
            Func<int, long, long> incrementStatusCodeCount)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _incrementStatusCodeCount = incrementStatusCodeCount ?? throw new ArgumentNullException(nameof(incrementStatusCodeCount));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _statusCode = statusCode;
            _metrics.RequestsByStatusCode.AddOrUpdate(statusCode, 1, _incrementStatusCodeCount);
            _inner.OnResponseStart(statusCode, headers, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (!chunk.IsEmpty)
                Interlocked.Add(ref _metrics.TotalBytesReceived, chunk.Length);

            _inner.OnResponseData(chunk, context);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            if (_statusCode >= 200 && _statusCode < 400)
                Interlocked.Increment(ref _metrics.SuccessfulRequests);
            else
                Interlocked.Increment(ref _metrics.FailedRequests);

            UpdateAverage(context);
            _inner.OnResponseEnd(trailers, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            Interlocked.Increment(ref _metrics.FailedRequests);
            UpdateAverage(context);
            _inner.OnResponseError(error, context);
        }

        private void UpdateAverage(RequestContext context)
        {
            _owner.RecordCompletion(context.Elapsed);
        }
    }
}
