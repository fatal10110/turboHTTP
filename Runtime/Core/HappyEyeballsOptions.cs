using System;

namespace TurboHTTP.Transport.Connection
{
    /// <summary>
    /// Options for Happy Eyeballs (RFC 8305) dual-stack connection racing.
    /// </summary>
    public sealed class HappyEyeballsOptions
    {
        private TimeSpan _familyStaggerDelay = TimeSpan.FromMilliseconds(250);
        private TimeSpan _attemptSpacingDelay = TimeSpan.FromMilliseconds(125);
        private int _maxConcurrentAttempts = 2;
        private bool _preferIpv6 = true;
        private bool _enable = true;

        // Set by every setter, even when it assigns the default value (#184): an explicitly
        // configured option is never treated as "untouched".
        private bool _explicitlySet;

        /// <summary>Delay before the first attempt of the second address family. Default 250 ms.</summary>
        public TimeSpan FamilyStaggerDelay
        {
            get => _familyStaggerDelay;
            set { _familyStaggerDelay = value; _explicitlySet = true; }
        }

        /// <summary>Delay between further connection attempts. Default 125 ms.</summary>
        public TimeSpan AttemptSpacingDelay
        {
            get => _attemptSpacingDelay;
            set { _attemptSpacingDelay = value; _explicitlySet = true; }
        }

        /// <summary>Maximum connection attempts in flight at once. Default 2.</summary>
        public int MaxConcurrentAttempts
        {
            get => _maxConcurrentAttempts;
            set { _maxConcurrentAttempts = value; _explicitlySet = true; }
        }

        /// <summary>Try IPv6 addresses first. Default true.</summary>
        public bool PreferIpv6
        {
            get => _preferIpv6;
            set { _preferIpv6 = value; _explicitlySet = true; }
        }

        /// <summary>Race address families; when false, addresses are tried one at a time. Default true.</summary>
        public bool Enable
        {
            get => _enable;
            set { _enable = value; _explicitlySet = true; }
        }

        /// <summary>Returns an independent copy of these options.</summary>
        public HappyEyeballsOptions Clone()
        {
            return new HappyEyeballsOptions
            {
                _familyStaggerDelay = _familyStaggerDelay,
                _attemptSpacingDelay = _attemptSpacingDelay,
                _maxConcurrentAttempts = _maxConcurrentAttempts,
                _preferIpv6 = _preferIpv6,
                _enable = _enable,
                _explicitlySet = _explicitlySet
            };
        }

        /// <summary>True when no option was ever assigned (assigning a default value counts as set).</summary>
        internal bool IsDefault()
        {
            return !_explicitlySet;
        }

        /// <summary>Throws when an option is out of range.</summary>
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
