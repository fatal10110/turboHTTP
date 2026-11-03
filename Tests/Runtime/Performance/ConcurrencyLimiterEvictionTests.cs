using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.RateLimit;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// #73: host entries are pinned while a permit is held or awaited, so eviction under host
    /// churn never orphans a semaphore (no SemaphoreFullException, no doubled per-host limit).
    /// </summary>
    public class ConcurrencyLimiterEvictionTests
    {
        private const int ChurnHosts = ConcurrencyLimiter.MaxHostSemaphoreEntries + 200;

        [Test]
        public void HeldPermit_SurvivesHostChurn_AndStillBlocksTheSameHost()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 4096);

                await limiter.AcquireAsync("victim");
                var waiter = limiter.AcquireAsync("victim");
                Assert.IsFalse(waiter.IsCompleted, "second acquire must wait for the held permit");

                for (int i = 0; i < ChurnHosts; i++)
                {
                    await limiter.AcquireAsync($"churn-{i}");
                    limiter.Release($"churn-{i}");
                }

                // The waiter is still queued on the original semaphore, not on a fresh one.
                Assert.IsFalse(waiter.IsCompleted, "eviction created a second semaphore for a pinned host");
                var third = limiter.AcquireAsync("victim");
                Assert.IsFalse(third.IsCompleted, "per-host limit of 1 exceeded after churn");

                limiter.Release("victim");
                await waiter;
                Assert.IsFalse(third.IsCompleted);
                limiter.Release("victim");
                await third;
                Assert.DoesNotThrow(() => limiter.Release("victim"));

                Assert.LessOrEqual(limiter.HostEntryCount, ConcurrencyLimiter.MaxHostSemaphoreEntries);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CancelledWaiter_UnpinsItsEntry_SoChurnCanEvictIt()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 4096);

                await limiter.AcquireAsync("victim");
                using (var cts = new CancellationTokenSource())
                {
                    var waiter = limiter.AcquireAsync("victim", cts.Token);
                    cts.Cancel();
                    try
                    {
                        await waiter;
                        Assert.Fail("cancelled waiter acquired a permit");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                limiter.Release("victim");

                for (int i = 0; i < ChurnHosts; i++)
                {
                    await limiter.AcquireAsync($"churn-{i}");
                    limiter.Release($"churn-{i}");
                }

                Assert.LessOrEqual(limiter.HostEntryCount, ConcurrencyLimiter.MaxHostSemaphoreEntries);

                // A fresh entry for the evicted host still enforces the limit.
                await limiter.AcquireAsync("victim");
                var blocked = limiter.AcquireAsync("victim");
                Assert.IsFalse(blocked.IsCompleted);
                limiter.Release("victim");
                await blocked;
                limiter.Release("victim");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HostChurnStress_NeverThrowsSemaphoreFull_AndNeverExceedsPerHostLimit()
        {
            Task.Run(async () =>
            {
                const int perHost = 2;
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: perHost, maxTotalConnections: 4096);
                var hotHosts = new[] { "hot-a", "hot-b", "hot-c" };
                var inFlight = new int[hotHosts.Length];
                var violations = new int[hotHosts.Length];
                var workers = new List<Task>();

                // Hot workers keep a few hosts busy while churn workers push the cache over its cap,
                // so eviction runs on (almost) every release while hot permits are held and awaited.
                for (int w = 0; w < 8; w++)
                {
                    int hostIndex = w % hotHosts.Length;
                    workers.Add(Task.Run(async () =>
                    {
                        for (int j = 0; j < 400; j++)
                        {
                            await limiter.AcquireAsync(hotHosts[hostIndex]).ConfigureAwait(false);
                            try
                            {
                                int now = Interlocked.Increment(ref inFlight[hostIndex]);
                                if (now > perHost)
                                    Interlocked.Increment(ref violations[hostIndex]);

                                await Task.Yield();
                                Interlocked.Decrement(ref inFlight[hostIndex]);
                            }
                            finally
                            {
                                limiter.Release(hotHosts[hostIndex]);
                            }
                        }
                    }));
                }

                for (int w = 0; w < 4; w++)
                {
                    int worker = w;
                    workers.Add(Task.Run(async () =>
                    {
                        for (int j = 0; j < ChurnHosts; j++)
                        {
                            var host = $"churn-{worker}-{j}";
                            await limiter.AcquireAsync(host).ConfigureAwait(false);
                            limiter.Release(host);
                        }
                    }));
                }

                await Task.WhenAll(workers);

                for (int i = 0; i < hotHosts.Length; i++)
                    Assert.AreEqual(0, violations[i], $"{hotHosts[i]} exceeded its per-host limit");
                Assert.LessOrEqual(limiter.HostEntryCount, ConcurrencyLimiter.MaxHostSemaphoreEntries);
            }).GetAwaiter().GetResult();
        }
    }
}
