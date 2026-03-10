using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Retry
{
    /// <summary>
    /// Interceptor that retries retryable requests with exponential backoff.
    /// </summary>
    public sealed class RetryInterceptor : IHttpInterceptor
    {
        private readonly Action<string> _log;
        private readonly RetryPolicy _policy;

        public RetryInterceptor(RetryPolicy policy = null, Action<string> log = null)
        {
            _policy = policy ?? RetryPolicy.Default;
            _log = log ?? (_ => { });
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                if (!ShouldAttemptRetry(request))
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var attempt = 0;
                var delay = _policy.InitialDelay;

                while (true)
                {
                    attempt++;
                    context.SetState("RetryAttempt", attempt);
                    context.RecordEvent("RetryAttempt", new Dictionary<string, object>
                    {
                        { "attempt", attempt }
                    });

                    var isLastAttempt = attempt > _policy.MaxRetries;
                    if (isLastAttempt)
                    {
                        var terminalObserver = new RetryTerminalObserverHandler(handler);
                        await next(request, terminalObserver, context, cancellationToken).ConfigureAwait(false);
                        if (attempt > 1)
                        {
                            if (terminalObserver.WasRetryableFailure)
                            {
                                context.RecordEvent("RetryExhausted", new Dictionary<string, object>
                                {
                                    { "attempts", attempt }
                                });
                            }
                            else if (terminalObserver.WasCommitted && !terminalObserver.DeliveredError)
                            {
                                context.RecordEvent("RetrySucceeded", new Dictionary<string, object>
                                {
                                    { "attempts", attempt }
                                });
                            }
                        }

                        return;
                    }

                    var detector = new RetryDetectorHandler(handler);
                    await next(request, detector, context, cancellationToken).ConfigureAwait(false);

                    if (!detector.WasRetryable)
                    {
                        if (attempt > 1 && detector.WasCommitted && !detector.DeliveredError)
                        {
                            context.RecordEvent("RetrySucceeded", new Dictionary<string, object>
                            {
                                { "attempts", attempt }
                            });
                        }

                        return;
                    }

                    context.RecordEvent("RetryScheduled", new Dictionary<string, object>
                    {
                        { "attempt", attempt },
                        { "delayMs", delay.TotalMilliseconds }
                    });
                    _log($"[RetryInterceptor] Attempt {attempt} failed, retrying in {delay.TotalSeconds:F1}s...");

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    delay = NextDelay(delay);
                }
            };
        }

        private bool ShouldAttemptRetry(UHttpRequest request)
        {
            if (_policy.MaxRetries == 0)
                return false;

            if (_policy.OnlyRetryIdempotent && !request.Method.IsIdempotent())
                return false;

            return true;
        }

        private TimeSpan NextDelay(TimeSpan current)
        {
            var next = current.TotalMilliseconds * _policy.BackoffMultiplier;
            return TimeSpan.FromMilliseconds(
                Math.Min(next, _policy.MaxDelay.TotalMilliseconds));
        }

        private sealed class RetryTerminalObserverHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;

            internal RetryTerminalObserverHandler(IHttpHandler inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            internal bool WasRetryableFailure { get; private set; }
            internal bool WasCommitted { get; private set; }
            internal bool DeliveredError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                _inner.OnRequestStart(request, context);
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
                WasCommitted = true;
                WasRetryableFailure = statusCode >= 500 && statusCode < 600;
                _inner.OnResponseStart(statusCode, headers, context);
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
                _inner.OnResponseData(chunk, context);
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                _inner.OnResponseEnd(trailers, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                DeliveredError = true;
                WasRetryableFailure = error?.HttpError != null && error.HttpError.IsRetryable();
                _inner.OnResponseError(error, context);
            }
        }
    }
}
