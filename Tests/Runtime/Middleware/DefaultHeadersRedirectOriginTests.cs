using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Regression tests for #787: credential default headers must never follow an automatic
    /// redirect to a different origin (RFC 9110 15.4), whichever order the DefaultHeaders and
    /// Redirect interceptors have.
    /// </summary>
    [TestFixture]
    public class DefaultHeadersRedirectOriginTests
    {
        public enum Order
        {
            RedirectOuterDefaultsInner,
            DefaultsOuterRedirectInner
        }

        private sealed class Hop
        {
            public string Uri;
            public string Authorization;
            public string ProxyAuthorization;
            public string Cookie;
            public string ApiKey;
            public string Accept;
        }

        [TestCase(Order.RedirectOuterDefaultsInner, false)]
        [TestCase(Order.RedirectOuterDefaultsInner, true)]
        [TestCase(Order.DefaultsOuterRedirectInner, false)]
        [TestCase(Order.DefaultsOuterRedirectInner, true)]
        public void CrossOriginRedirect_NeverCarriesCredentialDefaults(Order order, bool overrideExisting)
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, overrideExisting, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://other.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(2, hops.Count);
                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.AreEqual("Basic proxy-secret", hops[0].ProxyAuthorization);
                Assert.AreEqual("session=abc", hops[0].Cookie);

                Assert.AreEqual("https://other.test/final", hops[1].Uri);
                Assert.IsNull(hops[1].Authorization, "Authorization leaked to a different origin");
                Assert.IsNull(hops[1].ProxyAuthorization, "Proxy-Authorization leaked to a different origin");
                Assert.IsNull(hops[1].Cookie, "Cookie leaked to a different origin");

                // Non-credential defaults keep applying on every hop (RedirectInterceptor forwards
                // them in both orders).
                Assert.AreEqual("application/json", hops[1].Accept);
                Assert.AreEqual("key-1", hops[1].ApiKey);
            });
        }

        [TestCase(Order.RedirectOuterDefaultsInner)]
        [TestCase(Order.DefaultsOuterRedirectInner)]
        public void CrossOriginThenSameOriginHop_NeverCarriesCredentialDefaults(Order order)
        {
            // A -> B (cross-origin) -> B (same origin as the previous hop): still bound to A.
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, false, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://other.test/step",
                    ["https://other.test/step"] = "https://other.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(3, hops.Count);
                for (int i = 1; i < hops.Count; i++)
                {
                    Assert.IsNull(hops[i].Authorization, "hop " + i);
                    Assert.IsNull(hops[i].ProxyAuthorization, "hop " + i);
                    Assert.IsNull(hops[i].Cookie, "hop " + i);
                }
            });
        }

        [Test]
        public void RedirectBackToOriginalOrigin_CarriesCredentialDefaultsAgain()
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(Order.RedirectOuterDefaultsInner, false, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://other.test/step",
                    ["https://other.test/step"] = "https://origin.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(3, hops.Count);
                Assert.IsNull(hops[1].Authorization);
                Assert.AreEqual("Bearer secret-token", hops[2].Authorization);
                Assert.AreEqual("session=abc", hops[2].Cookie);
            });
        }

        [TestCase(Order.RedirectOuterDefaultsInner)]
        [TestCase(Order.DefaultsOuterRedirectInner)]
        public void SameOriginRedirect_KeepsCredentialDefaults(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, false, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://origin.test/next",
                    ["https://origin.test/next"] = "https://origin.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(3, hops.Count);
                for (int i = 0; i < hops.Count; i++)
                {
                    Assert.AreEqual("Bearer secret-token", hops[i].Authorization, "hop " + i);
                    Assert.AreEqual("Basic proxy-secret", hops[i].ProxyAuthorization, "hop " + i);
                    Assert.AreEqual("session=abc", hops[i].Cookie, "hop " + i);
                }
            });
        }

        [Test]
        public void SeparateRequests_EachBindToTheirOwnOrigin()
        {
            // The origin binding is per request (per RequestContext), not per interceptor instance.
            AssertAsync.Run(async () =>
            {
                var seen = new List<string>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    seen.Add(req.Headers.Get("Authorization"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), new DefaultHeadersInterceptor(CreateDefaults()) },
                    transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://b.test/"));
                using (await pipeline.ExecuteAsync(first, new RequestContext(first))) { }
                using (await pipeline.ExecuteAsync(second, new RequestContext(second))) { }

                Assert.AreEqual("Bearer secret-token", seen[0]);
                Assert.AreEqual("Bearer secret-token", seen[1]);
            });
        }

        private static HttpHeaders CreateDefaults()
        {
            var defaults = new HttpHeaders();
            defaults.Set("Authorization", "Bearer secret-token");
            defaults.Set("Proxy-Authorization", "Basic proxy-secret");
            defaults.Set("Cookie", "session=abc");
            defaults.Set("X-Api-Key", "key-1");
            defaults.Set("Accept", "application/json");
            return defaults;
        }

        private static async Task<List<Hop>> RunChainAsync(
            Order order,
            bool overrideExisting,
            Dictionary<string, string> redirects,
            string startUri)
        {
            var hops = new List<Hop>();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                hops.Add(new Hop
                {
                    Uri = req.Uri.AbsoluteUri,
                    Authorization = req.Headers.Get("Authorization"),
                    ProxyAuthorization = req.Headers.Get("Proxy-Authorization"),
                    Cookie = req.Headers.Get("Cookie"),
                    ApiKey = req.Headers.Get("X-Api-Key"),
                    Accept = req.Headers.Get("Accept")
                });

                if (redirects.TryGetValue(req.Uri.AbsoluteUri, out var location))
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", location);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                }

                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
            });

            var defaults = new DefaultHeadersInterceptor(CreateDefaults(), overrideExisting);
            var redirect = new RedirectInterceptor();
            var interceptors = order == Order.RedirectOuterDefaultsInner
                ? new IHttpInterceptor[] { redirect, defaults }
                : new IHttpInterceptor[] { defaults, redirect };

            var pipeline = new TestInterceptorPipeline(interceptors, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(startUri));
            using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return hops;
        }
    }
}
