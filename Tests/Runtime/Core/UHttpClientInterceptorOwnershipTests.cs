using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.RateLimit;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// The client disposes configured interceptors only when it owns them (#63):
    /// <see cref="UHttpClientOptions.DisposeInterceptors"/>, default false, mirrors DisposeTransport.
    /// </summary>
    [TestFixture]
    public class UHttpClientInterceptorOwnershipTests
    {
        [Test]
        public void DisposeInterceptors_DefaultsToFalse()
        {
            Assert.IsFalse(new UHttpClientOptions().DisposeInterceptors);
        }

        [Test]
        public void Clone_PreservesDisposeInterceptors()
        {
            Assert.IsTrue(new UHttpClientOptions { DisposeInterceptors = true }.Clone().DisposeInterceptors);
            Assert.IsFalse(new UHttpClientOptions().Clone().DisposeInterceptors);
        }

        [Test]
        public void Dispose_ByDefault_LeavesConfiguredInterceptorsUndisposed()
        {
            var interceptor = new DisposeTrackingInterceptor(new List<string>(), "i1");
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                FollowRedirects = false,
                Interceptors = { interceptor }
            });

            client.Dispose();

            Assert.AreEqual(0, interceptor.DisposeCount);
        }

        [Test]
        public void SharedInterceptors_DisposingOneClient_DoesNotBreakTheOther()
        {
            AssertAsync.Run(async () =>
            {
                var tracking = new DisposeTrackingInterceptor(new List<string>(), "tracking");
                var cache = new CacheInterceptor();
                var concurrency = new ConcurrencyInterceptor(maxConnectionsPerHost: 2, maxTotalConnections: 4);
                var options = new UHttpClientOptions
                {
                    Transport = new MockTransport(),
                    FollowRedirects = false,
                    Interceptors = { tracking, cache, concurrency }
                };

                var clientA = new UHttpClient(options);
                var clientB = new UHttpClient(options.Clone());
                try
                {
                    await SendAsync(clientA, "/a");
                    await SendAsync(clientB, "/b");

                    clientA.Dispose();

                    Assert.AreEqual(0, tracking.DisposeCount);
                    // Before #63 the cache and the concurrency limiter were disposed with client A and
                    // client B failed with ObjectDisposedException.
                    await SendAsync(clientB, "/c");
                    await SendAsync(clientB, "/d");
                }
                finally
                {
                    clientA.Dispose();
                    clientB.Dispose();
                    cache.Dispose();
                    concurrency.Dispose();
                }

                Assert.AreEqual(0, tracking.DisposeCount);
            });
        }

        [Test]
        public void DisposeInterceptorsTrue_DisposesEachOnce_InReverseOrder()
        {
            var order = new List<string>();
            var i1 = new DisposeTrackingInterceptor(order, "i1");
            var i2 = new DisposeTrackingInterceptor(order, "i2");
            var i3 = new DisposeTrackingInterceptor(order, "i3");
            var client = new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(),
                FollowRedirects = false,
                DisposeInterceptors = true,
                Interceptors = { i1, i2, i3 }
            });

            client.Dispose();
            client.Dispose();

            Assert.AreEqual(new[] { "i3", "i2", "i1" }, order.ToArray());
            Assert.AreEqual(1, i1.DisposeCount);
            Assert.AreEqual(1, i2.DisposeCount);
            Assert.AreEqual(1, i3.DisposeCount);
        }

        [Test]
        public void DisposeInterceptors_IsSnapshottedAtConstruction()
        {
            var interceptor = new DisposeTrackingInterceptor(new List<string>(), "i1");
            var options = new UHttpClientOptions
            {
                Transport = new MockTransport(),
                FollowRedirects = false,
                Interceptors = { interceptor }
            };
            var client = new UHttpClient(options);

            options.DisposeInterceptors = true;
            client.Dispose();

            Assert.AreEqual(0, interceptor.DisposeCount);
        }

        private static async Task SendAsync(UHttpClient client, string path)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test" + path));
            using var response = await client.SendBufferedAsync(request);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private sealed class DisposeTrackingInterceptor : IHttpInterceptor, IDisposable
        {
            private readonly List<string> _order;
            private readonly string _name;

            public DisposeTrackingInterceptor(List<string> order, string name)
            {
                _order = order;
                _name = name;
            }

            public int DisposeCount { get; private set; }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return next;
            }

            public void Dispose()
            {
                DisposeCount++;
                _order.Add(_name);
            }
        }
    }
}
