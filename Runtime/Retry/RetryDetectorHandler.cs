using System;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Retry
{
    internal sealed class RetryDetectorHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private bool _committed;

        internal RetryDetectorHandler(IHttpHandler inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        internal bool WasRetryable { get; private set; }
        internal bool WasCommitted => _committed;
        internal bool DeliveredError { get; private set; }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            if (statusCode >= 500 && statusCode < 600)
            {
                if (!context.GetState(TransportBehaviorFlags.SelfDrainsResponseBody, false))
                {
                    throw new InvalidOperationException(
                        "RetryDetectorHandler requires a transport that drains response bodies independently of handler callback forwarding.");
                }

                // Retryable 5xx responses are suppressed so the outer interceptor can re-dispatch.
                // This is only safe when the transport continues draining or aborting the response
                // body without relying on downstream handler consumption.
                WasRetryable = true;
                return;
            }

            _committed = true;
            _inner.OnResponseStart(statusCode, headers, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (_committed)
                _inner.OnResponseData(chunk, context);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            if (_committed)
                _inner.OnResponseEnd(trailers, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            if (error?.HttpError != null && error.HttpError.IsRetryable())
            {
                WasRetryable = true;
                if (_committed)
                {
                    DeliveredError = true;
                    _inner.OnResponseError(error, context);
                }

                return;
            }

            DeliveredError = true;
            _inner.OnResponseError(error, context);
        }
    }
}
