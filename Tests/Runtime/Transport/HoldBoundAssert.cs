using System;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// Load-proof checks for a release that must wait for a short bound (#1291). A read at a fixed
    /// point after the start races the bound when the test thread stalls; instead the release is
    /// awaited and the time of its first observation is compared with the bound. Load only delays
    /// that observation, so a loaded host cannot fail the check, while a release that no longer
    /// waits for the bound is seen by the first poll, long before half the bound.
    /// </summary>
    internal static class HoldBoundAssert
    {
        /// <summary>
        /// Awaits <paramref name="released"/> and fails if it was first observed before half of
        /// <paramref name="bound"/> elapsed on <paramref name="sinceStart"/> (started just before the
        /// call that arms the bound). Half the bound absorbs a timer that fires a tick early.
        /// </summary>
        internal static async Task ReleasedNotBeforeTheBoundAsync(
            Stopwatch sinceStart, TimeSpan bound, Func<bool> released, string what)
        {
            await AbandonedSocketIoTests.WaitUntilAsync(released, $"{what}: never released at the bound");
            TimeSpan observedAt = sinceStart.Elapsed;
            Assert.GreaterOrEqual(
                observedAt.TotalMilliseconds, bound.TotalMilliseconds / 2,
                $"{what}: released {observedAt.TotalMilliseconds:F0} ms after the start, before the {bound.TotalMilliseconds:F0} ms bound");
        }
    }
}
