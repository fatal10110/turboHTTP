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
        private static readonly TimeSpan ResponseDiscardTimeout = TimeSpan.FromSeconds(2);

        private readonly Action<string> _log;
        private readonly RetryPolicy _policy;

        /// <summary>
        /// Initializes a new instance of the <see cref="RetryInterceptor"/> class.
        /// </summary>
        public RetryInterceptor(RetryPolicy policy = null, Action<string> log = null)
        {
            _policy = policy != null ? new RetryPolicy(policy) : RetryPolicy.Default;
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
                var detector = new RetryDetectorHandler(handler, cancellationToken, ResponseDiscardTimeout);
                var terminalObserver = new RetryTerminalObserverHandler(handler);

                while (true)
                {
                    attempt++;
                    context.SetState("RetryAttempt", attempt);
                    RecordAttemptEvent(context, attempt);

                    var isLastAttempt = attempt > _policy.MaxRetries;
                    if (isLastAttempt)
                    {
                        terminalObserver.Reset(forwardRequestStart: false);
                        await next(request, terminalObserver, context, cancellationToken).ConfigureAwait(false);
                        if (attempt > 1)
                        {
                            if (terminalObserver.WasRetryableFailure)
                            {
                                RecordAttemptCountEvent(context, "RetryExhausted", attempt);
                            }
                            else if (terminalObserver.WasCommitted && !terminalObserver.DeliveredError)
                            {
                                RecordAttemptCountEvent(context, "RetrySucceeded", attempt);
                            }
                        }

                        return;
                    }

                    detector.Reset(forwardRequestStart: attempt == 1);
                    await next(request, detector, context, cancellationToken).ConfigureAwait(false);

                    if (detector.DeliveredError)
                        return;

                    if (!detector.WasRetryable)
                    {
                        if (attempt > 1 && detector.WasCommitted)
                            RecordAttemptCountEvent(context, "RetrySucceeded", attempt);

                        return;
                    }

                    var delay = _policy.ComputeDelay(attempt, detector.RetryAfterDelay);
                    context.RecordEvent("RetryScheduled", new Dictionary<string, object>
                    {
                        { "attempt", attempt },
                        { "delayMs", delay.TotalMilliseconds }
                    });
                    _log($"[RetryInterceptor] Attempt {attempt} failed, retrying in {delay.TotalSeconds:F1}s...");

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            };
        }

        private bool ShouldAttemptRetry(UHttpRequest request)
        {
            if (_policy.MaxRetries == 0)
                return false;

            if (request == null)
                return false;

            if (request.Content.Replayability == RequestBodyReplayability.NonReplayable)
                return false;

            if (_policy.OnlyRetryIdempotent && !request.Method.IsIdempotent())
                return false;

            return true;
        }

        private static void RecordAttemptEvent(RequestContext context, int attempt)
        {
            context.RecordEvent("RetryAttempt", new Dictionary<string, object>
            {
                { "attempt", attempt }
            });
        }

        private static void RecordAttemptCountEvent(RequestContext context, string eventName, int attemptCount)
        {
            context.RecordEvent(eventName, new Dictionary<string, object>
            {
                { "attempts", attemptCount }
            });
        }

        private sealed class RetryTerminalObserverHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private bool _forwardRequestStart;

            internal RetryTerminalObserverHandler(IHttpHandler inner)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            internal bool WasRetryableFailure { get; private set; }
            internal bool WasCommitted { get; private set; }
            internal bool DeliveredError { get; private set; }

            internal void Reset(bool forwardRequestStart)
            {
                WasRetryableFailure = false;
                WasCommitted = false;
                DeliveredError = false;
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

            public ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                WasCommitted = true;
                WasRetryableFailure |= statusCode >= 500 && statusCode < 600;
                return _inner.OnResponseStartAsync(statusCode, headers, body, context);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                DeliveredError = true;
                WasRetryableFailure |= error?.HttpError != null && error.HttpError.IsRetryable();
                _inner.OnResponseError(error, context);
            }
        }
    }
}
