using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Observability
{
    internal sealed class MetricsHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly MetricsInterceptor _owner;
        private readonly HttpMetrics _metrics;
        private readonly Func<int, long, long> _incrementStatusCodeCount;
        private readonly UHttpRequest _request;

        private int _statusCode;
        private int _completionRecorded;

        internal MetricsHandler(
            IHttpHandler inner,
            MetricsInterceptor owner,
            HttpMetrics metrics,
            Func<int, long, long> incrementStatusCodeCount,
            UHttpRequest request)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _incrementStatusCodeCount = incrementStatusCodeCount ?? throw new ArgumentNullException(nameof(incrementStatusCodeCount));
            _request = request ?? throw new ArgumentNullException(nameof(request));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            _statusCode = statusCode;
            _metrics.RequestsByStatusCode.AddOrUpdate(statusCode, 1, _incrementStatusCodeCount);
            var finalizeOnReturn = true;
            var bodyToForward = body;
            if (body != null)
            {
                if (body.TryGetBufferedData(out var data) && !data.IsEmpty)
                {
                    Interlocked.Add(ref _metrics.TotalBytesReceived, data.Length);
                }
                else
                {
                    finalizeOnReturn = false;
                    bodyToForward = new ObservedResponseBodySource(
                        body,
                        chunk =>
                        {
                            if (!chunk.IsEmpty)
                                Interlocked.Add(ref _metrics.TotalBytesReceived, chunk.Length);
                        },
                        completion => CompleteResponse(context, completion.Error));
                }
            }

            try
            {
                await _inner.OnResponseStartAsync(statusCode, headers, bodyToForward, context).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CompleteResponse(context, ex);
                throw;
            }

            if (finalizeOnReturn)
                CompleteResponse(context, error: null);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            CompleteResponse(context, error);
            _inner.OnResponseError(error, context);
        }

        private void CompleteResponse(RequestContext context, Exception error)
        {
            if (Interlocked.Exchange(ref _completionRecorded, 1) != 0)
                return;

            var sentBytes = context.GetState<long>(TransportBehaviorFlags.RequestBodyBytesSent, default);
            if (sentBytes > 0)
            {
                Interlocked.Add(ref _metrics.TotalBytesSent, sentBytes);
            }
            else if (_request.Content.Length.GetValueOrDefault() > 0)
            {
                Interlocked.Add(ref _metrics.TotalBytesSent, _request.Content.Length.Value);
            }

            if (error == null && _statusCode >= 200 && _statusCode < 400)
                Interlocked.Increment(ref _metrics.SuccessfulRequests);
            else
                Interlocked.Increment(ref _metrics.FailedRequests);

            _owner.RecordCompletion(context.Elapsed);
        }
    }
}
