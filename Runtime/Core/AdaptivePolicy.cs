using System;

namespace TurboHTTP.Core
{
    public sealed class AdaptivePolicy
    {
        public bool Enable { get; set; }
        public TimeSpan MinTimeout { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool AllowConcurrencyAdjustment { get; set; } = true;
        public bool AllowRetryAdjustment { get; set; } = true;

        public AdaptivePolicy Clone()
        {
            return new AdaptivePolicy
            {
                Enable = Enable,
                MinTimeout = MinTimeout,
                MaxTimeout = MaxTimeout,
                AllowConcurrencyAdjustment = AllowConcurrencyAdjustment,
                AllowRetryAdjustment = AllowRetryAdjustment
            };
        }
    }
}
