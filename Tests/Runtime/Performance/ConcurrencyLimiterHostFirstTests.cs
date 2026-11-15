using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.RateLimit;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Regression tests for #508: ConcurrencyLimiter takes the per-host permit before the global
    /// one, so waiters queued behind a saturated host hold no global permit and idle hosts are
    /// still admitted. Uncontended SemaphoreSlim waits complete synchronously, so admission is
    /// observable through Task.IsCompleted without timing.
    /// </summary>
    [TestFixture]
    public class ConcurrencyLimiterHostFirstTests
    {
        [Test]
        public void IdleHost_IsAdmitted_WhileSaturatedHostHasMoreWaitersThanGlobalLimit()
        {
            AssertAsync.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 3);
                using var waitersCts = new CancellationTokenSource();

                await limiter.AcquireAsync("a.test");

                // More waiters on the saturated host than the global limit.
                var waiters = new Task[4];
                for (int i = 0; i < waiters.Length; i++)
                    waiters[i] = limiter.AcquireAsync("a.test", waitersCts.Token);

                for (int i = 0; i < waiters.Length; i++)
                    Assert.IsFalse(waiters[i].IsCompleted, "host a.test is saturated");

                var idleHost = limiter.AcquireAsync("b.test");
                Assert.IsTrue(idleHost.IsCompleted, "a request to an idle host must not wait behind a saturated host");
                await idleHost;

                waitersCts.Cancel();
                for (int i = 0; i < waiters.Length; i++)
                    await ExpectAsync<OperationCanceledException>(waiters[i]);

                limiter.Release("b.test");
                limiter.Release("a.test");

                AssertFullCapacity(limiter);
            });
        }

        [Test]
        public void CancelledWhileWaitingForGlobal_ReleasesTheHostPermitItTook()
        {
            AssertAsync.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 1);
                using var cts = new CancellationTokenSource();

                await limiter.AcquireAsync("x.test");

                // Takes a.test's only host permit, then waits for the global one.
                var waiter = limiter.AcquireAsync("a.test", cts.Token);
                Assert.IsFalse(waiter.IsCompleted);

                cts.Cancel();
                await ExpectAsync<OperationCanceledException>(waiter);

                limiter.Release("x.test");

                var again = limiter.AcquireAsync("a.test");
                Assert.IsTrue(again.IsCompleted, "the cancelled waiter must return the host permit it held");
                await again;
                limiter.Release("a.test");

                AssertFullCapacity(limiter);
            });
        }

        [Test]
        public void DisposedWhileWaitingForGlobal_ThrowsObjectDisposed()
        {
            AssertAsync.Run(async () =>
            {
                var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 1);
                await limiter.AcquireAsync("x.test");

                var waiter = limiter.AcquireAsync("a.test");
                Assert.IsFalse(waiter.IsCompleted);

                limiter.Dispose();
                await ExpectAsync<ObjectDisposedException>(waiter);

                // The permit holder may still release after dispose without throwing.
                Assert.DoesNotThrow(() => limiter.Release("x.test"));
            });
        }

        // Accepts T or a subtype (a cancelled SemaphoreSlim wait may surface as TaskCanceledException).
        private static async Task ExpectAsync<T>(Task task) where T : Exception
        {
            try
            {
                await task;
            }
            catch (T)
            {
                return;
            }

            Assert.Fail("Expected " + typeof(T).Name);
        }

        // Every host permit and every global permit is free again: one acquire per global slot on
        // distinct hosts, plus the hosts used above, all complete synchronously.
        private static void AssertFullCapacity(ConcurrencyLimiter limiter)
        {
            var hosts = new[] { "a.test", "b.test", "x.test", "c.test" };
            int count = Math.Min(limiter.MaxTotalConnections, hosts.Length);
            for (int i = 0; i < count; i++)
            {
                var acquire = limiter.AcquireAsync(hosts[i]);
                Assert.IsTrue(acquire.IsCompleted, "permit for " + hosts[i] + " was not returned");
            }

            for (int i = 0; i < count; i++)
                limiter.Release(hosts[i]);
        }
    }
}
