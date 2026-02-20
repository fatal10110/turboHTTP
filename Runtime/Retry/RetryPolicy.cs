using System;

namespace TurboHTTP.Retry
{
    /// <summary>
    /// Configuration for retry behavior.
    /// Timeout semantics: request timeout is evaluated per attempt, not as a total retry budget.
    /// </summary>
    public class RetryPolicy
    {
        private static readonly RetryPolicy DefaultPolicy = new RetryPolicy();
        private static readonly RetryPolicy NoRetryPolicy = new RetryPolicy { MaxRetries = 0 };

        /// <summary>
        /// Maximum number of retry attempts after the initial request.
        /// Default: 3 (total of 4 attempts including the original).
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Initial delay before the first retry. Subsequent delays are
        /// multiplied by BackoffMultiplier.
        /// </summary>
        public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Multiplier applied to delay after each retry attempt.
        /// Default: 2.0 (exponential backoff: 1s, 2s, 4s, ...).
        /// </summary>
        public double BackoffMultiplier { get; set; } = 2.0;

        /// <summary>
        /// Maximum delay between retries. Prevents unbounded growth
        /// with high BackoffMultiplier or MaxRetries values.
        /// Default: 30 seconds.
        /// </summary>
        public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// When true, only retry idempotent methods (GET, HEAD, PUT, DELETE, OPTIONS).
        /// POST and PATCH are not retried to prevent duplicate side effects.
        /// </summary>
        public bool OnlyRetryIdempotent { get; set; } = true;

        /// <summary>
        /// Default retry policy: 3 retries, 1s initial delay, 2x backoff, idempotent only.
        /// </summary>
        public static RetryPolicy Default => DefaultPolicy;

        /// <summary>
        /// No retry policy: disables all retries.
        /// </summary>
        public static RetryPolicy NoRetry => NoRetryPolicy;
    }
}
