using System;
using System.Diagnostics;

namespace TurboHTTP.Transport.Http2
{
    internal static class Http2MonotonicClock
    {
        // Unity 2021.3's .NET Standard 2.1 profile does not expose Environment.TickCount64.
        // HTTP/2 stall detection only needs a monotonic clock and elapsed comparisons.
        private static readonly double TimestampTicksPerMillisecond =
            Stopwatch.Frequency / 1000d;

        internal static long GetTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        internal static bool HasElapsedMilliseconds(
            long startTimestamp,
            long nowTimestamp,
            long timeoutMilliseconds)
        {
            if (timeoutMilliseconds <= 0)
                return true;

            long elapsedTimestampTicks = nowTimestamp - startTimestamp;
            if (elapsedTimestampTicks <= 0)
                return false;

            return elapsedTimestampTicks >= ToTimestampTicks(timeoutMilliseconds);
        }

        private static long ToTimestampTicks(long milliseconds)
        {
            if (milliseconds <= 0)
                return 0;

            double timestampTicks = milliseconds * TimestampTicksPerMillisecond;
            if (timestampTicks >= long.MaxValue)
                return long.MaxValue;

            return (long)Math.Ceiling(timestampTicks);
        }
    }
}
