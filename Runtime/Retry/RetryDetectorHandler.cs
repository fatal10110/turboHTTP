using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Retry
{
    internal sealed class RetryDetectorHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CancellationToken _dispatchCancellationToken;
        private readonly TimeSpan _responseDiscardTimeout;
        private bool _committed;
        private bool _forwardRequestStart;

        internal RetryDetectorHandler(
            IHttpHandler inner,
            CancellationToken dispatchCancellationToken,
            TimeSpan responseDiscardTimeout)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _dispatchCancellationToken = dispatchCancellationToken;
            if (responseDiscardTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(responseDiscardTimeout));

            _responseDiscardTimeout = responseDiscardTimeout;
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

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            if (statusCode >= 500 && statusCode < 600)
            {
                // Retryable 5xx responses are suppressed so the outer interceptor can re-dispatch.
                WasRetryable = true;
                RetryAfterDelay = ParseRetryAfter(headers);
                if (body != null)
                    await DiscardBodyAsync(body).ConfigureAwait(false);
                return;
            }

            _committed = true;
            await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
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

        private async ValueTask DiscardBodyAsync(IResponseBodySource body)
        {
            if (body == null)
                return;

            var drained = false;
            var aborted = false;
            CancellationTokenSource discardTimeoutCts = null;
            try
            {
                discardTimeoutCts = _dispatchCancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_dispatchCancellationToken)
                    : new CancellationTokenSource();
                discardTimeoutCts.CancelAfter(_responseDiscardTimeout);

                try
                {
                    await body.DrainAsync(discardTimeoutCts.Token).ConfigureAwait(false);
                    drained = true;
                }
                catch (OperationCanceledException) when (_dispatchCancellationToken.IsCancellationRequested)
                {
                    body.Abort();
                    aborted = true;
                    throw;
                }
                catch
                {
                    body.Abort();
                    aborted = true;
                }
            }
            finally
            {
                discardTimeoutCts?.Dispose();

                try
                {
                    if (!drained && !aborted)
                    {
                        body.Abort();
                        aborted = true;
                    }

                    await body.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    if (!aborted)
                    {
                        try
                        {
                            body.Abort();
                        }
                        catch
                        {
                        }
                    }
                }
            }
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
