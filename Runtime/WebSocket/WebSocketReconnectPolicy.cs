using System;
using System.Security.Cryptography;

namespace TurboHTTP.WebSocket
{
    public sealed class WebSocketReconnectPolicy
    {
        private readonly Func<WebSocketCloseCode, bool> _reconnectOnCloseCode;
        private readonly Random _jitterRng;
        private readonly object _rngLock = new object();

        public static readonly WebSocketReconnectPolicy None = new WebSocketReconnectPolicy(maxRetries: 0);

        public static readonly WebSocketReconnectPolicy Default = new WebSocketReconnectPolicy();

        public static readonly WebSocketReconnectPolicy Infinite = new WebSocketReconnectPolicy(maxRetries: -1);

        public WebSocketReconnectPolicy(
            int maxRetries = 5,
            TimeSpan? initialDelay = null,
            TimeSpan? maxDelay = null,
            double backoffMultiplier = 2.0,
            double jitterFactor = 0.1,
            Func<WebSocketCloseCode, bool> reconnectOnCloseCode = null)
        {
            if (maxRetries < -1)
                throw new ArgumentOutOfRangeException(nameof(maxRetries), "Must be -1, 0, or greater.");

            TimeSpan resolvedInitialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
            TimeSpan resolvedMaxDelay = maxDelay ?? TimeSpan.FromSeconds(30);

            if (resolvedInitialDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be greater than zero.");
            if (resolvedMaxDelay <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be greater than zero.");
            if (resolvedMaxDelay < resolvedInitialDelay)
                throw new ArgumentOutOfRangeException(nameof(maxDelay), "Max delay must be >= initial delay.");
            if (backoffMultiplier < 1.0)
                throw new ArgumentOutOfRangeException(nameof(backoffMultiplier), "Backoff multiplier must be >= 1.0.");
            if (jitterFactor < 0.0 || jitterFactor > 1.0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(jitterFactor),
                    "Jitter factor must be between 0.0 and 1.0.");
            }

            MaxRetries = maxRetries;
            InitialDelay = resolvedInitialDelay;
            MaxDelay = resolvedMaxDelay;
            BackoffMultiplier = backoffMultiplier;
            JitterFactor = jitterFactor;
            _reconnectOnCloseCode = reconnectOnCloseCode ?? DefaultReconnectOnCloseCode;

            var seedBytes = new byte[4];
            using (var rng = RandomNumberGenerator.Create())
            {
                if (rng == null)
                    throw new PlatformNotSupportedException("Random number generator is unavailable.");

                rng.GetBytes(seedBytes);
            }

            int seed = BitConverter.ToInt32(seedBytes, 0);
            _jitterRng = new Random(seed);
        }

        public int MaxRetries { get; }

        public TimeSpan InitialDelay { get; }

        public TimeSpan MaxDelay { get; }

        public double BackoffMultiplier { get; }

        public double JitterFactor { get; }

        public bool Enabled => MaxRetries != 0;

        public TimeSpan ComputeDelay(int attempt)
        {
            if (attempt < 1)
                throw new ArgumentOutOfRangeException(nameof(attempt), "Attempt must be >= 1.");

            double initialDelayMs = InitialDelay.TotalMilliseconds;
            double cappedMaxMs = MaxDelay.TotalMilliseconds;

            double rawDelayMs;
            if (attempt == 1)
            {
                rawDelayMs = initialDelayMs;
            }
            else
            {
                double power = Math.Pow(BackoffMultiplier, attempt - 1);
                rawDelayMs = initialDelayMs * power;
            }

            if (rawDelayMs > cappedMaxMs)
                rawDelayMs = cappedMaxMs;

            if (JitterFactor > 0d && rawDelayMs > 0d)
            {
                double jitterRange = rawDelayMs * JitterFactor;
                double jitter;
                lock (_rngLock)
                {
                    jitter = (_jitterRng.NextDouble() * 2d - 1d) * jitterRange;
                }

                rawDelayMs += jitter;
                if (rawDelayMs < 0d)
                    rawDelayMs = 0d;
            }

            if (rawDelayMs > cappedMaxMs)
                rawDelayMs = cappedMaxMs;

            return TimeSpan.FromMilliseconds(rawDelayMs);
        }

        public bool ShouldReconnect(int attempt, WebSocketCloseCode? closeCode)
        {
            if (!Enabled)
                return false;

            if (attempt < 1)
                return false;

            if (MaxRetries > 0 && attempt > MaxRetries)
                return false;

            if (closeCode.HasValue && !_reconnectOnCloseCode(closeCode.Value))
                return false;

            return true;
        }

        private static bool DefaultReconnectOnCloseCode(WebSocketCloseCode closeCode)
        {
            return closeCode == WebSocketCloseCode.GoingAway ||
                   closeCode == WebSocketCloseCode.AbnormalClosure ||
                   closeCode == WebSocketCloseCode.InternalServerError;
        }
    }
}
