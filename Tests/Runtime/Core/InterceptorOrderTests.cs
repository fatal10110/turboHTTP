using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.RateLimit;
using TurboHTTP.Retry;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Regression tests for #55: the four interceptor-order failure modes. Cache-outside-Auth and
    /// Concurrency-outside-Retry are rejected when the client is built (or a plugin registers);
    /// Redirect/Auth and Redirect/Cookie are safe in either order, which
    /// <c>AuthRedirectOrderTests</c> (#56) and <c>CookieRedirectHopTests</c> (#58) prove on the wire.
    /// </summary>
    [TestFixture]
    public class InterceptorOrderTests
    {
        [Test]
        public void CacheOutsideAuth_IsRejectedAtConstruction()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new CacheInterceptor(new CachePolicy()),
                new AuthInterceptor(new StaticTokenProvider("secret-token"))));

            StringAssert.Contains("CacheInterceptor", ex.Message);
            StringAssert.Contains("AuthInterceptor", ex.Message);
            StringAssert.DoesNotContain("secret-token", ex.Message);
        }

        [Test]
        public void AuthOutsideCache_CacheSeesAuthorizationAndDoesNotStore()
        {
            AssertAsync.Run(async () =>
            {
                var transport = CacheableTransport();
                using var client = CreateClient(
                    transport,
                    new AuthInterceptor(new StaticTokenProvider("secret-token")),
                    new CacheInterceptor(new CachePolicy { AllowCacheForAuthorizedRequests = false }));

                using (var first = await client.Get("https://example.test/me").SendBufferedAsync())
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                using (var second = await client.Get("https://example.test/me").SendBufferedAsync())
                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);

                // The authorized response was never served from the cache.
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void ConcurrencyOutsideRetry_IsRejectedAtConstruction()
        {
            var ex = Assert.Throws<ArgumentException>(() => CreateClient(
                new ConcurrencyInterceptor(),
                new RetryInterceptor()));

            StringAssert.Contains("ConcurrencyInterceptor", ex.Message);
            StringAssert.Contains("RetryInterceptor", ex.Message);
        }

        [Test]
        public void RetryOutsideConcurrency_IsAccepted()
        {
            Assert.DoesNotThrow(() => CreateClient(new RetryInterceptor(), new ConcurrencyInterceptor()).Dispose());
        }

        [Test]
        public void RejectedOrder_IsReportedEvenWithInterceptorsBetween()
        {
            Assert.Throws<ArgumentException>(() => CreateClient(
                new ConcurrencyInterceptor(),
                new DecompressionInterceptor(),
                new RetryInterceptor()));
        }

        [Test]
        public void RedirectAndAuth_EitherOrderIsAccepted()
        {
            // Wire behavior of both orders: AuthRedirectOrderTests (#56).
            Assert.DoesNotThrow(() => CreateClient(
                new RedirectInterceptor(),
                new AuthInterceptor(new StaticTokenProvider("t"))).Dispose());
            Assert.DoesNotThrow(() => CreateClient(
                new AuthInterceptor(new StaticTokenProvider("t")),
                new RedirectInterceptor()).Dispose());
        }

        [Test]
        public void RedirectAndCookie_EitherOrderIsAccepted()
        {
            // Wire behavior of both orders: CookieRedirectHopTests (#58).
            Assert.DoesNotThrow(() => CreateClient(new RedirectInterceptor(), new CookieInterceptor()).Dispose());
            Assert.DoesNotThrow(() => CreateClient(new CookieInterceptor(), new RedirectInterceptor()).Dispose());
        }

        [Test]
        public void CanonicalOrder_IsAccepted()
        {
            Assert.DoesNotThrow(() => CreateClient(
                new RedirectInterceptor(),
                new CookieInterceptor(),
                new AuthInterceptor(new StaticTokenProvider("t")),
                new CacheInterceptor(new CachePolicy()),
                new RetryInterceptor(),
                new DecompressionInterceptor(),
                new ConcurrencyInterceptor()).Dispose());
        }

        [Test]
        public void PluginContributingAuthInsideBaseCache_IsRejectedAndShutDown()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(new CacheInterceptor(new CachePolicy()));
                // Not every capability: the contribution is wrapped, and the wrapper must keep its role.
                var plugin = new ContributingPlugin(
                    "auth-plugin",
                    new AuthInterceptor(new StaticTokenProvider("t")),
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses);

                var error = await TestHelpers.AssertThrowsAsync<PluginException>(
                    () => client.RegisterPluginAsync(plugin));

                StringAssert.Contains("CacheInterceptor", error.Message);
                Assert.AreEqual(0, client.GetRegisteredPlugins().Count);
                Assert.AreEqual(1, plugin.ShutdownCount, "an initialized but rejected plugin must be shut down");
            });
        }

        [Test]
        public void PluginContributingCacheInsideBaseAuth_IsAccepted()
        {
            AssertAsync.Run(async () =>
            {
                using var client = CreateClient(new AuthInterceptor(new StaticTokenProvider("t")));
                var plugin = new ContributingPlugin(
                    "cache-plugin",
                    new CacheInterceptor(new CachePolicy()),
                    PluginCapabilities.MutateRequests | PluginCapabilities.MutateResponses);

                await client.RegisterPluginAsync(plugin);

                Assert.AreEqual(1, client.GetRegisteredPlugins().Count);
            });
        }

        private static UHttpClient CreateClient(params IHttpInterceptor[] interceptors)
        {
            return CreateClient(new MockTransport(), interceptors);
        }

        private static UHttpClient CreateClient(MockTransport transport, params IHttpInterceptor[] interceptors)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor>(interceptors)
            });
        }

        private static MockTransport CacheableTransport()
        {
            var headers = new HttpHeaders();
            headers.Set("Cache-Control", "max-age=60");
            return new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("per-user"));
        }

        private sealed class ContributingPlugin : IHttpPlugin
        {
            private readonly IHttpInterceptor _interceptor;
            private int _shutdownCount;

            public ContributingPlugin(string name, IHttpInterceptor interceptor, PluginCapabilities capabilities)
            {
                Name = name;
                _interceptor = interceptor;
                Capabilities = capabilities;
            }

            public string Name { get; }
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities { get; }
            public int ShutdownCount => Volatile.Read(ref _shutdownCount);

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(_interceptor);
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref _shutdownCount);
                return default;
            }
        }
    }
}
