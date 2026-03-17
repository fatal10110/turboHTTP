using System;
using System.Globalization;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Retry
{
    internal sealed class RetryDetectorHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private bool _committed;
        private bool _forwardRequestStart;

        internal RetryDetectorHandler(IHttpHandler inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        internal bool WasRetryable { get; private set; }
        internal bool WasCommitted => _committed;
        internal bool DeliveredError { get; private set; }
        internal TimeSpan? RetryAfterDelay { get; private set; }

        internal void Reset(bool forwardRequestStart)
        {
            WasRetryable = false;
            _committed = false;
            DeliveredError = false;
            RetryAfterDelay = null;
            _forwardRequestStart = forwardRequestStart;
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            if (_forwardRequestStart)
            {
                _forwardRequestStart = false;
                _inner.OnRequestStart(request, context);
            }
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
                RetryAfterDelay = ParseRetryAfter(headers);
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

        private static TimeSpan? ParseRetryAfter(HttpHeaders headers)
        {
            if (headers == null)
                return null;

            var values = headers.GetValues("Retry-After");
            for (int i = 0; i < values.Count; i++)
            {
                string value = values[i];
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
                    seconds >= 0)
                {
                    return TimeSpan.FromSeconds(seconds);
                }

                if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var retryAt))
                {
                    var delay = retryAt - DateTimeOffset.UtcNow;
                    return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                }
            }

            return null;
        }
    }
}
