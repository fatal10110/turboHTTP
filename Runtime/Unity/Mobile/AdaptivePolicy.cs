using System;

namespace TurboHTTP.Unity.Mobile
{
    /// <summary>
    /// Options for <see cref="AdaptiveInterceptor"/>. Adaptation is timeout-only: when enabled,
    /// a request without an explicit timeout gets its timeout scaled by the current
    /// <see cref="NetworkQuality"/> and clamped to [<see cref="MinTimeout"/>, <see cref="MaxTimeout"/>].
    /// Concurrency and retry policies are not adapted.
    /// </summary>
    public sealed class AdaptivePolicy
    {
        /// <summary>Turns timeout adaptation on. Off by default.</summary>
        public bool Enable { get; set; }

        /// <summary>Lower bound of an adapted timeout. Default 2 seconds.</summary>
        public TimeSpan MinTimeout { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>Upper bound of an adapted timeout. Default 30 seconds.</summary>
        public TimeSpan MaxTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Returns an independent copy of this policy.</summary>
        public AdaptivePolicy Clone()
        {
            return new AdaptivePolicy
            {
                Enable = Enable,
                MinTimeout = MinTimeout,
                MaxTimeout = MaxTimeout
            };
        }
    }
}
