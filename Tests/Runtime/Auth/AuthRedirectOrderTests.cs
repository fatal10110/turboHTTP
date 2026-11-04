using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    /// <summary>
    /// Regression tests for #56: the bearer token must never follow an automatic redirect to a
    /// different origin (RFC 9110 15.4), whichever order the Auth and Redirect interceptors have.
    /// </summary>
    [TestFixture]
    public class AuthRedirectOrderTests
    {
        public enum Order
        {
            RedirectOuterAuthInner,
            AuthOuterRedirectInner
        }

        private sealed class Hop
        {
            public string Uri;
            public string Authorization;
        }

        [TestCase(Order.RedirectOuterAuthInner)]
        [TestCase(Order.AuthOuterRedirectInner)]
        public void CrossOriginRedirect_NeverCarriesToken(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://other.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(2, hops.Count);
                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.AreEqual("https://other.test/final", hops[1].Uri);
                Assert.IsNull(hops[1].Authorization, "token leaked to a different origin");
            });
        }

        [TestCase(Order.RedirectOuterAuthInner)]
        [TestCase(Order.AuthOuterRedirectInner)]
        public void CrossOriginThenSameOriginHop_NeverCarriesToken(Order order)
        {
            // A -> B (cross-origin) -> B (same origin as the previous hop). The per-hop cross-site
            // flag is false on the last hop; the token is still bound to A.
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://other.test/step",
                    ["https://other.test/step"] = "https://other.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(3, hops.Count);
                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.IsNull(hops[1].Authorization);
                Assert.AreEqual("https://other.test/final", hops[2].Uri);
                Assert.IsNull(hops[2].Authorization, "token leaked after a same-origin hop on the foreign host");
            });
        }

        [TestCase(Order.RedirectOuterAuthInner)]
        [TestCase(Order.AuthOuterRedirectInner)]
        public void DifferentPortOrScheme_IsCrossOrigin(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://origin.test:8443/final"
                }, "https://origin.test/start");

                Assert.AreEqual(2, hops.Count);
                Assert.IsNull(hops[1].Authorization);
            });
        }

        [TestCase(Order.RedirectOuterAuthInner)]
        [TestCase(Order.AuthOuterRedirectInner)]
        public void SameOriginRedirect_KeepsToken(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var hops = await RunChainAsync(order, new Dictionary<string, string>
                {
                    ["https://origin.test/start"] = "https://origin.test/next",
                    ["https://origin.test/next"] = "https://origin.test/final"
                }, "https://origin.test/start");

                Assert.AreEqual(3, hops.Count);
                for (int i = 0; i < hops.Count; i++)
                    Assert.AreEqual("Bearer secret-token", hops[i].Authorization, "hop " + i);
            });
        }

        [Test]
        public void SeparateRequests_EachBindToTheirOwnOrigin()
        {
            // The origin binding is per request (per RequestContext), not per interceptor instance.
            AssertAsync.Run(async () =>
            {
                var seen = new List<Hop>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    seen.Add(new Hop { Uri = req.Uri.AbsoluteUri, Authorization = req.Headers.Get("Authorization") });
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var auth = new AuthInterceptor(new StaticTokenProvider("secret-token"));
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), auth }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://b.test/"));
                await pipeline.ExecuteAsync(first, new RequestContext(first));
                await pipeline.ExecuteAsync(second, new RequestContext(second));

                Assert.AreEqual("Bearer secret-token", seen[0].Authorization);
                Assert.AreEqual("Bearer secret-token", seen[1].Authorization);
            });
        }

        private static async Task<List<Hop>> RunChainAsync(
            Order order,
            Dictionary<string, string> redirects,
            string startUri)
        {
            var hops = new List<Hop>();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                hops.Add(new Hop
                {
                    Uri = req.Uri.AbsoluteUri,
                    Authorization = req.Headers.Get("Authorization")
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

            var auth = new AuthInterceptor(new StaticTokenProvider("secret-token"));
            var redirect = new RedirectInterceptor();
            var interceptors = order == Order.RedirectOuterAuthInner
                ? new IHttpInterceptor[] { redirect, auth }
                : new IHttpInterceptor[] { auth, redirect };

            var pipeline = new TestInterceptorPipeline(interceptors, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(startUri));
            var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            return hops;
        }
    }
}
