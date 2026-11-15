using System;

namespace TurboHTTP.Cache
{
    /// <summary>
    /// Peer-controlled cache lifetimes (RFC 9111 section 1.2.2 delta-seconds) and the overflow-safe DateTime
    /// arithmetic applied to them.
    /// </summary>
    internal static class CacheDeltaSeconds
    {
        /// <summary>
        /// RFC 9111 section 1.2.2: a delta-seconds value greater than 2147483647, or one the recipient cannot
        /// represent, MUST be treated as 2147483648 (2^31).
        /// </summary>
        internal const long MaxSeconds = 2147483648L;

        internal static readonly TimeSpan MaxLifetime = TimeSpan.FromTicks(MaxSeconds * TimeSpan.TicksPerSecond);

        /// <summary>
        /// Parses a delta-seconds value (1*DIGIT; surrounding whitespace and a quoted-string form are tolerated).
        /// Any number of digits is accepted: the value saturates at <see cref="MaxSeconds"/> instead of overflowing.
        /// Returns false for an empty value or any non-digit character, so the directive is ignored.
        /// </summary>
        internal static bool TryParse(string value, out long seconds)
        {
            seconds = 0;
            if (value == null)
                return false;

            var text = value.Trim().Trim('"');
            if (text.Length == 0)
                return false;

            long result = 0;
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < '0' || c > '9')
                    return false;

                // Saturate before the multiply can overflow; the remaining characters must still be digits.
                if (result < MaxSeconds)
                {
                    result = (result * 10) + (c - '0');
                    if (result > MaxSeconds)
                        result = MaxSeconds;
                }
            }

            seconds = result;
            return true;
        }

        /// <summary>Parses a delta-seconds value into a lifetime of at most <see cref="MaxLifetime"/>.</summary>
        internal static bool TryParseLifetime(string value, out TimeSpan lifetime)
        {
            if (!TryParse(value, out var seconds))
            {
                lifetime = default;
                return false;
            }

            lifetime = TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
            return true;
        }

        /// <summary>Clamps a lifetime to [0, <see cref="MaxLifetime"/>].</summary>
        internal static TimeSpan ClampLifetime(TimeSpan lifetime)
        {
            if (lifetime <= TimeSpan.Zero)
                return TimeSpan.Zero;

            return lifetime > MaxLifetime ? MaxLifetime : lifetime;
        }

        /// <summary>
        /// <paramref name="utc"/> + <paramref name="offset"/>, saturating at <see cref="DateTime.MinValue"/> and
        /// <see cref="DateTime.MaxValue"/> instead of throwing.
        /// </summary>
        internal static DateTime AddClamped(DateTime utc, TimeSpan offset)
        {
            // utc.Ticks is in [0, DateTime.MaxValue.Ticks], so neither bound below can overflow.
            if (offset.Ticks > DateTime.MaxValue.Ticks - utc.Ticks)
                return DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

            if (offset.Ticks < DateTime.MinValue.Ticks - utc.Ticks)
                return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);

            return utc.Add(offset);
        }
    }
}
