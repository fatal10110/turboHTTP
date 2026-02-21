using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Retry
{
    /// <summary>
    /// Middleware that automatically retries failed requests with exponential backoff.
    /// Retries on 5xx server errors and retryable transport errors.
    /// Only retries idempotent methods by default.
    /// Note: request-level timeout applies to each attempt independently.
    /// </summary>
    public class RetryMiddleware : IHttpMiddleware
    {
        private readonly Action<string> _log;
        private readonly RetryPolicy _policy;

        public RetryMiddleware(RetryPolicy policy = null, Action<string> log = null)
        {
            _policy = policy ?? RetryPolicy.Default;
            _log = log ?? (_ => { });
        }

        public async ValueTask<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (!ShouldRetry(request))
            {
                return await next(request, context, cancellationToken);
            }

            int attempt = 0;
            TimeSpan delay = _policy.InitialDelay;

            while (true)
            {
                attempt++;
                context.SetState("RetryAttempt", attempt);
                context.RecordEvent($"RetryAttempt{attempt}");

                try
                {
                    var response = await next(request, context, cancellationToken);

                    // Success or non-retryable status
                    if (response.IsSuccessStatusCode || !IsRetryableResponse(response))
                    {
                        if (attempt > 1)
                        {
                            context.RecordEvent("RetrySucceeded",
                                new System.Collections.Generic.Dictionary<string, object>
                                {
                                    { "attempts", attempt }
                                });
                        }
                        return response;
                    }

                    // Retryable error — check if retries exhausted
                    if (attempt > _policy.MaxRetries)
                    {
                        context.RecordEvent("RetryExhausted");
                        return response; // Return last failed response
                    }

                    response.Dispose();

                    // Wait before retry
                    _log($"[RetryMiddleware] Attempt {attempt} failed with {(int)response.StatusCode}, retrying in {delay.TotalSeconds:F1}s...");
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(
                        Math.Min(delay.TotalMilliseconds * _policy.BackoffMultiplier,
                                 _policy.MaxDelay.TotalMilliseconds));
                }
                catch (UHttpException ex) when (ex.HttpError.IsRetryable())
                {
                    if (attempt > _policy.MaxRetries)
                    {
                        context.RecordEvent("RetryExhausted");
                        throw;
                    }

                    _log($"[RetryMiddleware] Attempt {attempt} failed with {ex.HttpError.Type}, retrying in {delay.TotalSeconds:F1}s...");
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromMilliseconds(
                        Math.Min(delay.TotalMilliseconds * _policy.BackoffMultiplier,
                                 _policy.MaxDelay.TotalMilliseconds));
                }
            }
        }

        private bool ShouldRetry(UHttpRequest request)
        {
            if (_policy.MaxRetries == 0)
                return false;

            if (_policy.OnlyRetryIdempotent && !request.Method.IsIdempotent())
                return false;

            return true;
        }

        private bool IsRetryableResponse(UHttpResponse response)
        {
            if (response.Error != null)
                return response.Error.IsRetryable();

            // Retry on 5xx server errors
            int statusCode = (int)response.StatusCode;
            return statusCode >= 500 && statusCode < 600;
        }
    }
}
