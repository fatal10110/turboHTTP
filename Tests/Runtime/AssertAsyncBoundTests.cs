using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace TurboHTTP.Tests
{
    /// <summary>
    /// #330: a body that never completes fails its test within the bound instead of blocking the
    /// calling (in PlayMode, the Unity main) thread forever. The NUnit timeout is the backstop
    /// that turns a regression into a failure here too.
    /// </summary>
    [TestFixture]
    public class AssertAsyncBoundTests
    {
        private static readonly TimeSpan ShortBound = TimeSpan.FromMilliseconds(100);

        [Test]
        [Timeout(10000)]
        public void Run_BodyNeverCompletes_FailsWithinTheBound()
        {
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var ex = Assert.Throws<AssertAsyncTimeoutException>(
                () => AssertAsync.Run(() => never.Task, ShortBound));

            StringAssert.Contains("did not complete", ex.Message);
            Assert.IsFalse(never.Task.IsCompleted);
        }

        [Test]
        [Timeout(10000)]
        public void ThrowsAsync_BodyNeverCompletes_IsNotMistakenForTheExpectedException()
        {
            var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Even ThrowsAsync<Exception> must not accept the bound failure as its expectation.
            Assert.Throws<AssertAsyncTimeoutException>(
                () => AssertAsync.ThrowsAsync<Exception>(() => never.Task, ShortBound));
        }

        [Test]
        [Timeout(10000)]
        public void Run_BodyCompletesWithinTheBound_PropagatesItsResult()
        {
            AssertAsync.Run(async () => await Task.Yield(), TimeSpan.FromSeconds(5));

            var ex = Assert.Throws<InvalidOperationException>(() => AssertAsync.Run(async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            }, TimeSpan.FromSeconds(5)));
            Assert.AreEqual("boom", ex.Message);
        }

        [Test]
        public void DefaultBound_IsFiniteAndAboveTheSlowestBoundedBody()
        {
            Assert.Greater(AssertAsync.DefaultBound, TimeSpan.FromSeconds(60));
            Assert.Less(AssertAsync.DefaultBound, TimeSpan.FromMinutes(30));
        }
    }
}
