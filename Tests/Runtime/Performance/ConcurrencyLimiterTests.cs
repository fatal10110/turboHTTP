using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Performance;

namespace TurboHTTP.Tests.Performance
{
    public class ConcurrencyLimiterTests
    {
        [Test]
        public void Constructor_ThrowsOnInvalidArgs()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ConcurrencyLimiter(maxConnectionsPerHost: 0));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ConcurrencyLimiter(maxTotalConnections: 0));
        }

        [Test]
        public void AcquireAsync_ThrowsOnNullHost()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter();

                try
                {
                    await limiter.AcquireAsync(null);
                    Assert.Fail("Expected ArgumentNullException");
                }
                catch (ArgumentNullException)
                {
                    // Expected
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Release_ThrowsOnNullHost()
        {
            using var limiter = new ConcurrencyLimiter();
            Assert.Throws<ArgumentNullException>(() => limiter.Release(null));
        }

        [Test]
        public void AcquireAndRelease_BasicFlow()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 2);

                await limiter.AcquireAsync("host1");
                await limiter.AcquireAsync("host1");

                limiter.Release("host1");
                limiter.Release("host1");

                // Should be able to acquire again after releasing
                await limiter.AcquireAsync("host1");
                limiter.Release("host1");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AcquireAsync_EnforcesPerHostLimit()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 2, maxTotalConnections: 100);

                await limiter.AcquireAsync("host1");
                await limiter.AcquireAsync("host1");

                // Third acquire should block - test with a short timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

                try
                {
                    await limiter.AcquireAsync("host1", cts.Token);
                    Assert.Fail("Expected OperationCanceledException");
                }
                catch (OperationCanceledException)
                {
                    // Expected - per-host limit enforced
                }

                limiter.Release("host1");
                limiter.Release("host1");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AcquireAsync_EnforcesGlobalLimit()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 10, maxTotalConnections: 3);

                await limiter.AcquireAsync("host1");
                await limiter.AcquireAsync("host2");
                await limiter.AcquireAsync("host3");

                // Fourth acquire should block (global limit = 3)
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

                try
                {
                    await limiter.AcquireAsync("host4", cts.Token);
                    Assert.Fail("Expected OperationCanceledException");
                }
                catch (OperationCanceledException)
                {
                    // Expected - global limit enforced
                }

                limiter.Release("host1");
                limiter.Release("host2");
                limiter.Release("host3");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AcquireAsync_Cancellation_DoesNotLeakPermit()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 1, maxTotalConnections: 1);

                // Fill the limiter
                await limiter.AcquireAsync("host1");

                // Cancel a waiting acquire
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                try
                {
                    await limiter.AcquireAsync("host1", cts.Token);
                }
                catch (OperationCanceledException) { }

                // Release the original
                limiter.Release("host1");

                // Should be able to acquire again (no leaked permit)
                using var cts2 = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                await limiter.AcquireAsync("host1", cts2.Token);
                limiter.Release("host1");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Dispose_IsIdempotent()
        {
            var limiter = new ConcurrencyLimiter();
            limiter.Dispose();
            Assert.DoesNotThrow(() => limiter.Dispose());
        }

        [Test]
        public void AcquireAsync_ThrowsAfterDispose()
        {
            Task.Run(async () =>
            {
                var limiter = new ConcurrencyLimiter();
                limiter.Dispose();

                try
                {
                    await limiter.AcquireAsync("host1");
                    Assert.Fail("Expected ObjectDisposedException");
                }
                catch (ObjectDisposedException)
                {
                    // Expected
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ConcurrentAcquireRelease_IsThreadSafe()
        {
            Task.Run(async () =>
            {
                using var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 4, maxTotalConnections: 16);
                var tasks = new List<Task>();

                for (int i = 0; i < 50; i++)
                {
                    var host = $"host{i % 5}";
                    tasks.Add(Task.Run(async () =>
                    {
                        for (int j = 0; j < 20; j++)
                        {
                            await limiter.AcquireAsync(host);
                            await Task.Yield();
                            limiter.Release(host);
                        }
                    }));
                }

                await Task.WhenAll(tasks);
                // If we get here without deadlock or exception, the test passes
            }).GetAwaiter().GetResult();
        }
    }
}
