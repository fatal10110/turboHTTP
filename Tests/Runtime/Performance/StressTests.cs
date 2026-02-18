using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Performance;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Performance
{
    /// <summary>
    /// Stress tests for Phase 6 performance gates.
    /// These tests validate correctness under high concurrency
    /// and check for resource leaks.
    /// </summary>
    public class StressTests
    {
        [Test]
        public void HighConcurrency_MockTransport_1000Requests()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport(HttpStatusCode.OK);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false
                });

                var tasks = new List<Task<UHttpResponse>>();

                for (int i = 0; i < 1000; i++)
                {
                    tasks.Add(client.Get($"https://test.com/api/{i}").SendAsync());
                }

                var responses = await Task.WhenAll(tasks);

                Assert.AreEqual(1000, responses.Length);
                Assert.AreEqual(1000, transport.RequestCount);

                foreach (var response in responses)
                {
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HighConcurrency_WithConcurrencyMiddleware()
        {
            Task.Run(async () =>
            {
                int maxConcurrent = 0;
                int currentConcurrent = 0;
                var lockObj = new object();

                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    var current = Interlocked.Increment(ref currentConcurrent);
                    lock (lockObj)
                    {
                        if (current > maxConcurrent) maxConcurrent = current;
                    }

                    await Task.Delay(1, ct); // Simulate work

                    Interlocked.Decrement(ref currentConcurrent);

                    return new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req);
                });

                var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 4, maxTotalConnections: 8);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false,
                    Middlewares = new List<IHttpMiddleware>
                    {
                        new ConcurrencyMiddleware(limiter)
                    }
                });

                var tasks = new List<Task<UHttpResponse>>();

                for (int i = 0; i < 100; i++)
                {
                    tasks.Add(client.Get("https://test.com/api/data").SendAsync());
                }

                await Task.WhenAll(tasks);

                // Verify concurrency limit was enforced
                Assert.LessOrEqual(maxConcurrent, 4,
                    $"Max concurrent requests ({maxConcurrent}) exceeded per-host limit (4)");
                Assert.AreEqual(100, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void HighConcurrency_MultipleHosts_ConcurrencyLimiter()
        {
            Task.Run(async () =>
            {
                var perHostMax = new Dictionary<string, int>();
                var perHostCurrent = new Dictionary<string, int>();
                var lockObj = new object();

                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    var host = req.Uri.Host;

                    lock (lockObj)
                    {
                        if (!perHostCurrent.ContainsKey(host))
                            perHostCurrent[host] = 0;
                        if (!perHostMax.ContainsKey(host))
                            perHostMax[host] = 0;

                        perHostCurrent[host]++;
                        if (perHostCurrent[host] > perHostMax[host])
                            perHostMax[host] = perHostCurrent[host];
                    }

                    await Task.Delay(5, ct);

                    lock (lockObj)
                    {
                        perHostCurrent[host]--;
                    }

                    return new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req);
                });

                var limiter = new ConcurrencyLimiter(maxConnectionsPerHost: 3, maxTotalConnections: 20);

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false,
                    Middlewares = new List<IHttpMiddleware>
                    {
                        new ConcurrencyMiddleware(limiter)
                    }
                });

                var tasks = new List<Task<UHttpResponse>>();
                var hosts = new[] { "host1.com", "host2.com", "host3.com" };

                for (int i = 0; i < 90; i++)
                {
                    var host = hosts[i % hosts.Length];
                    tasks.Add(client.Get($"https://{host}/api/{i}").SendAsync());
                }

                await Task.WhenAll(tasks);

                foreach (var kvp in perHostMax)
                {
                    Assert.LessOrEqual(kvp.Value, 3,
                        $"Host {kvp.Key} had {kvp.Value} concurrent requests, limit is 3");
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ObjectPool_NoLeaksUnderConcurrency()
        {
            Task.Run(async () =>
            {
                int factoryCalls = 0;
                var pool = new ObjectPool<List<int>>(
                    () => { Interlocked.Increment(ref factoryCalls); return new List<int>(); },
                    capacity: 16,
                    reset: list => list.Clear());

                var tasks = new List<Task>();

                for (int i = 0; i < 50; i++)
                {
                    tasks.Add(Task.Run(() =>
                    {
                        for (int j = 0; j < 200; j++)
                        {
                            var item = pool.Rent();
                            item.Add(j);
                            pool.Return(item);
                        }
                    }));
                }

                await Task.WhenAll(tasks);

                // Pool should not have more than capacity items
                Assert.LessOrEqual(pool.Count, 16);

                // Verify no cross-request data leakage (reset callback clears lists)
                for (int i = 0; i < pool.Count; i++)
                {
                    var item = pool.Rent();
                    Assert.AreEqual(0, item.Count,
                        "Pooled item should have been reset (cleared)");
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RequestQueue_PriorityOrdering()
        {
            Task.Run(async () =>
            {
                var queue = new RequestQueue<string>();

                queue.Enqueue("low1", RequestPriority.Low);
                queue.Enqueue("normal1", RequestPriority.Normal);
                queue.Enqueue("high1", RequestPriority.High);
                queue.Enqueue("normal2", RequestPriority.Normal);
                queue.Enqueue("high2", RequestPriority.High);
                queue.Enqueue("low2", RequestPriority.Low);

                Assert.AreEqual(6, queue.Count);

                // High priority items should come first
                Assert.AreEqual("high1", await queue.DequeueAsync());
                Assert.AreEqual("high2", await queue.DequeueAsync());

                // Then normal
                Assert.AreEqual("normal1", await queue.DequeueAsync());
                Assert.AreEqual("normal2", await queue.DequeueAsync());

                // Then low
                Assert.AreEqual("low1", await queue.DequeueAsync());
                Assert.AreEqual("low2", await queue.DequeueAsync());

                Assert.AreEqual(0, queue.Count);

                queue.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RequestQueue_ShutdownPreventsEnqueue()
        {
            var queue = new RequestQueue<string>();
            queue.Enqueue("item1");
            queue.Shutdown();

            Assert.IsTrue(queue.IsShutdown);
            Assert.Throws<InvalidOperationException>(() => queue.Enqueue("item2"));

            queue.Dispose();
        }

        [Test]
        public void RequestQueue_Shutdown_UnblocksPendingDequeue()
        {
            Task.Run(async () =>
            {
                var queue = new RequestQueue<string>();
                try
                {
                    var blockedDequeue = queue.DequeueAsync();

                    // Ensure the dequeue has reached wait state before shutdown.
                    await Task.Delay(25);
                    queue.Shutdown();

                    var completed = await Task.WhenAny(blockedDequeue, Task.Delay(1000));
                    Assert.AreSame(blockedDequeue, completed,
                        "Blocked dequeue should be released promptly after shutdown.");

                    try
                    {
                        await blockedDequeue;
                        Assert.Fail("Expected OperationCanceledException");
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                }
                finally
                {
                    queue.Dispose();
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RequestQueue_ForceShutdown_CancelsDequeueImmediately()
        {
            Task.Run(async () =>
            {
                var queue = new RequestQueue<string>();
                try
                {
                    queue.Enqueue("pending");
                    queue.ForceShutdown();

                    var dequeueTask = queue.DequeueAsync();
                    var completed = await Task.WhenAny(dequeueTask, Task.Delay(1000));
                    Assert.AreSame(dequeueTask, completed,
                        "Dequeue should complete immediately after ForceShutdown.");

                    try
                    {
                        await dequeueTask;
                        Assert.Fail("Expected OperationCanceledException");
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                }
                finally
                {
                    queue.Dispose();
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void UHttpClient_ThrowsAfterDispose()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport();
                var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false
                });

                client.Dispose();

                Assert.Throws<ObjectDisposedException>(() => client.Get("https://test.com"));
                Assert.Throws<ObjectDisposedException>(() => client.Post("https://test.com"));

                try
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                    await client.SendAsync(request);
                    Assert.Fail("Expected ObjectDisposedException");
                }
                catch (ObjectDisposedException)
                {
                    // Expected
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void UHttpClient_DoubleDispose_IsIdempotent()
        {
            var transport = new MockTransport();
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = false
            });

            client.Dispose();
            Assert.DoesNotThrow(() => client.Dispose());
        }

        [Test]
        public void ConcurrencyMiddleware_OwnedLimiter_DisposedWithClient()
        {
            Task.Run(async () =>
            {
                var middleware = new ConcurrencyMiddleware(maxConnectionsPerHost: 1, maxTotalConnections: 1);
                Assert.IsTrue(middleware is IDisposable);

                var transport = new MockTransport(HttpStatusCode.OK);
                var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = false,
                    Middlewares = new List<IHttpMiddleware> { middleware }
                });

                client.Dispose();

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                try
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        (req, ctx, ct) => Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            new HttpHeaders(),
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req)),
                        CancellationToken.None);
                    Assert.Fail("Expected ObjectDisposedException");
                }
                catch (ObjectDisposedException)
                {
                    // Expected
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ByteArrayPool_RentAndReturn()
        {
            var buffer = ByteArrayPool.Rent(1024);
            Assert.IsNotNull(buffer);
            Assert.GreaterOrEqual(buffer.Length, 1024);

            Assert.DoesNotThrow(() => ByteArrayPool.Return(buffer));
        }

        [Test]
        public void ByteArrayPool_ClearOnReturn()
        {
            var buffer = ByteArrayPool.Rent(64);

            // Write some data
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = 0xFF;

            // Return with clear
            ByteArrayPool.Return(buffer, clearArray: true);

            // Rent again — ArrayPool may return the same buffer
            var buffer2 = ByteArrayPool.Rent(64);
            if (ReferenceEquals(buffer, buffer2))
            {
                // If same buffer, it should be cleared
                for (int i = 0; i < 64; i++)
                {
                    Assert.AreEqual(0, buffer2[i],
                        $"Buffer byte {i} should be zeroed after clearArray return");
                }
            }

            ByteArrayPool.Return(buffer2);
        }

        [Test]
        public void ByteArrayPool_ZeroLength_ReturnsEmpty()
        {
            var buffer = ByteArrayPool.Rent(0);
            Assert.IsNotNull(buffer);
            Assert.AreEqual(0, buffer.Length);
        }

        [Test]
        public void ByteArrayPool_NegativeLength_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ByteArrayPool.Rent(-1));
        }

        [Test]
        public void ByteArrayPool_NullReturn_IsIgnored()
        {
            Assert.DoesNotThrow(() => ByteArrayPool.Return(null));
        }
    }
}
