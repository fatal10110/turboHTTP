using System;

namespace TurboHTTP.Transport.Connection
{
    public sealed class HappyEyeballsOptions
    {
        public TimeSpan FamilyStaggerDelay { get; set; } = TimeSpan.FromMilliseconds(250);
        public TimeSpan AttemptSpacingDelay { get; set; } = TimeSpan.FromMilliseconds(125);
        public int MaxConcurrentAttempts { get; set; } = 2;
        public bool PreferIpv6 { get; set; } = true;
        public bool Enable { get; set; } = true;

        public HappyEyeballsOptions Clone()
        {
            return new HappyEyeballsOptions
            {
                FamilyStaggerDelay = FamilyStaggerDelay,
                AttemptSpacingDelay = AttemptSpacingDelay,
                MaxConcurrentAttempts = MaxConcurrentAttempts,
                PreferIpv6 = PreferIpv6,
                Enable = Enable
            };
        }

        public void Validate()
        {
            if (FamilyStaggerDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(FamilyStaggerDelay), "Must be >= 0.");
            if (AttemptSpacingDelay < TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(AttemptSpacingDelay), "Must be >= 0.");
            if (MaxConcurrentAttempts <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxConcurrentAttempts), "Must be > 0.");
        }
    }
}
