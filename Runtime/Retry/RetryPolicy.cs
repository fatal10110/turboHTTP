using System;
using System.Threading;

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
        private static int _threadSeed = Environment.TickCount;

        [ThreadStatic]
        private static Random _threadJitterRng;

        private int _maxRetries = 3;
        private TimeSpan _initialDelay = TimeSpan.FromSeconds(1);
        private double _backoffMultiplier = 2.0;
        private TimeSpan _maxDelay = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Initializes a new instance of the <see cref="RetryPolicy"/> class.
        /// </summary>
        public RetryPolicy()
        {
        }

        internal RetryPolicy(RetryPolicy source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            _maxRetries = source._maxRetries;
            _initialDelay = source._initialDelay;
            _backoffMultiplier = source._backoffMultiplier;
            _maxDelay = source._maxDelay;
            OnlyRetryIdempotent = source.OnlyRetryIdempotent;
            UseJitter = source.UseJitter;
        }

        /// <summary>
        /// Maximum number of retry attempts after the initial request.
        /// Default: 3 (total of 4 attempts including the original).
        /// </summary>
        public int MaxRetries
        {
            get => _maxRetries;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(MaxRetries), "Max retries must be >= 0.");

                _maxRetries = value;
            }
        }

        /// <summary>
        /// Initial delay before the first retry. Subsequent delays are
        /// multiplied by BackoffMultiplier.
        /// </summary>
        public TimeSpan InitialDelay
        {
            get => _initialDelay;
            set
            {
                if (value < TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(InitialDelay), "Initial delay must be >= 0.");

                _initialDelay = value;
            }
        }

        /// <summary>
        /// Multiplier applied to delay after each retry attempt.
        /// Default: 2.0 (exponential backoff: 1s, 2s, 4s, ...).
        /// </summary>
        public double BackoffMultiplier
        {
            get => _backoffMultiplier;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(BackoffMultiplier),
                        "Backoff multiplier must be a finite value greater than zero.");
                }

                _backoffMultiplier = value;
            }
        }

        /// <summary>
        /// Maximum delay between retries. Prevents unbounded growth
        /// with high BackoffMultiplier or MaxRetries values.
        /// Default: 30 seconds.
        /// </summary>
        public TimeSpan MaxDelay
        {
            get => _maxDelay;
            set
            {
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(MaxDelay), "Max delay must be > 0.");

                _maxDelay = value;
            }
        }

        /// <summary>
        /// When true, only retry idempotent methods (GET, HEAD, PUT, DELETE, OPTIONS).
        /// POST and PATCH are not retried to prevent duplicate side effects.
        /// </summary>
        public bool OnlyRetryIdempotent { get; set; } = true;

        /// <summary>
        /// When true, the computed retry delay is randomized to reduce synchronized retry storms.
        /// </summary>
        public bool UseJitter { get; set; } = true;

        /// <summary>
        /// Default retry policy: 3 retries, 1s initial delay, 2x backoff, idempotent only.
        /// </summary>
        public static RetryPolicy Default => new RetryPolicy(DefaultPolicy);

        /// <summary>
        /// No retry policy: disables all retries.
        /// </summary>
        public static RetryPolicy NoRetry => new RetryPolicy(NoRetryPolicy);

        internal TimeSpan ComputeDelay(int attempt, TimeSpan? minimumDelayOverride = null)
        {
            if (attempt < 1)
                throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be >= 1.");

            double delayMs = InitialDelay.TotalMilliseconds;
            if (attempt > 1 && delayMs > 0d)
            {
                double power = Math.Pow(BackoffMultiplier, attempt - 1);
                delayMs *= power;
            }

            double maxDelayMs = MaxDelay.TotalMilliseconds;
            if (delayMs > maxDelayMs)
                delayMs = maxDelayMs;

            if (UseJitter && delayMs > 0d)
            {
                double halfDelayMs = delayMs / 2d;
                delayMs = halfDelayMs + (NextThreadJitter() * halfDelayMs);
            }

            if (minimumDelayOverride.HasValue && minimumDelayOverride.Value > TimeSpan.Zero)
            {
                double overrideDelayMs = minimumDelayOverride.Value.TotalMilliseconds;
                if (overrideDelayMs > delayMs)
                    delayMs = overrideDelayMs;
            }

            return TimeSpan.FromMilliseconds(delayMs);
        }

        private static double NextThreadJitter()
        {
            var rng = _threadJitterRng;
            if (rng == null)
            {
                int seed = Interlocked.Increment(ref _threadSeed) ^ Environment.CurrentManagedThreadId;
                rng = new Random(seed);
                _threadJitterRng = rng;
            }

            return rng.NextDouble();
        }
    }
}
