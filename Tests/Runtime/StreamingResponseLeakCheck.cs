using System;
using NUnit.Framework;
using TurboHTTP.Core;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// Attributes a dropped <see cref="UHttpStreamingResponse"/> to the test that dropped it (#366).
    /// The response finalizer only logs a warning, under whichever later test is running when the GC
    /// collects it; a fixture calls <see cref="Begin"/> from <c>[SetUp]</c> and
    /// <see cref="AssertNoLeakSince"/> from <c>[TearDown]</c> so the leaking test fails instead.
    /// Each call forces a full collection, so only fixtures that create streaming responses use it.
    /// </summary>
    internal static class StreamingResponseLeakCheck
    {
        /// <summary>
        /// Finalizes everything already unreachable (leaks of earlier tests are counted before this
        /// test starts) and returns the current count of undisposed finalized responses.
        /// </summary>
        internal static int Begin()
        {
            CollectAndFinalize();
            return UHttpStreamingResponse.UndisposedFinalizedCountForTesting;
        }

        /// <summary>
        /// Fails when a response became unreachable without being disposed since <see cref="Begin"/>.
        /// </summary>
        internal static void AssertNoLeakSince(int countAtBegin)
        {
            CollectAndFinalize();
            int leaked = UHttpStreamingResponse.UndisposedFinalizedCountForTesting - countAtBegin;
            if (leaked > 0)
            {
                Assert.Fail(leaked + " UHttpStreamingResponse instance(s) were finalized without Dispose during this test (#366). "
                    + "Dispose every streaming response on every path (testing.md).");
            }
        }

        private static void CollectAndFinalize()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
