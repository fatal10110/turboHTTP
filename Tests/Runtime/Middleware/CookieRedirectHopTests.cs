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
    /// Regression tests for #58: Set-Cookie received on each redirect hop is stored for the host of
    /// that hop (RFC 6265 section 5.3), whichever order the Cookie and Redirect interceptors have.
    /// </summary>
    [TestFixture]
    public class CookieRedirectHopTests
    {
        public enum Order
        {
            RedirectOuterCookieInner,
            CookieOuterRedirectInner
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void CrossHostRedirect_StoresEachHopsCookiesForThatHost(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                await RunChainAsync(order, jar, new Dictionary<string, (string Location, string SetCookie)>
                {
                    ["https://a.test/start"] = ("https://b.test/final", "a_sid=1; Path=/"),
                    ["https://b.test/final"] = (null, "b_sid=2; Path=/")
                }, "https://a.test/start");

                Assert.AreEqual("a_sid=1", Header(jar, "https://a.test/"));
                Assert.AreEqual("b_sid=2", Header(jar, "https://b.test/"));
            });
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void MultiHopRedirect_StoresIntermediateAndFinalHopCookies(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                await RunChainAsync(order, jar, new Dictionary<string, (string Location, string SetCookie)>
                {
                    ["https://a.test/start"] = ("https://b.test/step", "a_sid=1; Path=/"),
                    ["https://b.test/step"] = ("https://c.test/docs/final", "b_sid=2; Path=/"),
                    ["https://c.test/docs/final"] = (null, "c_sid=3")
                }, "https://a.test/start");

                Assert.AreEqual("a_sid=1", Header(jar, "https://a.test/"));
                Assert.AreEqual("b_sid=2", Header(jar, "https://b.test/"));
                // Default path of the final hop's request-uri (/docs), host-only on c.test.
                Assert.AreEqual("c_sid=3", Header(jar, "https://c.test/docs/page"));
                Assert.IsNull(Header(jar, "https://c.test/other"));
                Assert.AreEqual(3, jar.Count);
            });
        }

        [Test]
        public void CookieOuter_SecondDispatchOnSameContext_StoresEachResponseOnce()
        {
            // A retry interceptor re-runs the Cookie interceptor with the same RequestContext; the
            // redirect-hop observer must not be registered twice (double store per 3xx).
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                var transport = new MockTransport((req, ctx, ct) => Task.FromResult(Respond(req, ctx,
                    req.Uri.Host == "a.test" ? "https://b.test/final" : null,
                    req.Uri.Host == "a.test" ? "a_sid=1; Path=/" : "b_sid=2; Path=/")));

                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new CookieInterceptor(jar), new RedirectInterceptor() }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/start"));
                var context = new RequestContext(request);
                await pipeline.ExecuteAsync(request, context);
                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(4, CountCookieStored(context), "two dispatches x (one 3xx hop + one final response)");
                Assert.AreEqual("a_sid=1", Header(jar, "https://a.test/"));
                Assert.AreEqual("b_sid=2", Header(jar, "https://b.test/"));
                Assert.AreEqual(2, jar.Count);
            });
        }

        [Test]
        public void RedirectOuter_SecondDispatchOnSameContext_StoresEachResponseOnce()
        {
            // [Redirect, Cookie] with a retry-style second dispatch on the same RequestContext: the
            // inner Cookie interceptor must not be snapshotted as an outer redirect-response observer
            // by the second redirect handler (each 3xx Set-Cookie would be stored twice).
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                var transport = new MockTransport((req, ctx, ct) => Task.FromResult(Respond(req, ctx,
                    req.Uri.Host == "a.test" ? "https://b.test/final" : null,
                    req.Uri.Host == "a.test" ? "a_sid=1; Path=/" : "b_sid=2; Path=/")));

                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), new CookieInterceptor(jar) }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/start"));
                var context = new RequestContext(request);
                await pipeline.ExecuteAsync(request, context);
                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(4, CountCookieStored(context), "two dispatches x (one 3xx hop + one final response)");
                Assert.AreEqual("a_sid=1", Header(jar, "https://a.test/"));
                Assert.AreEqual("b_sid=2", Header(jar, "https://b.test/"));
                Assert.AreEqual(2, jar.Count);
            });
        }

        private static int CountCookieStored(RequestContext context)
        {
            var stored = 0;
            foreach (var e in context.Timeline)
            {
                if (e.Name == "CookieStored")
                    stored++;
            }

            return stored;
        }

        private static async Task RunChainAsync(
            Order order,
            CookieJar jar,
            Dictionary<string, (string Location, string SetCookie)> script,
            string startUri)
        {
            var transport = new MockTransport((req, ctx, ct) =>
            {
                var step = script[req.Uri.AbsoluteUri];
                return Task.FromResult(Respond(req, ctx, step.Location, step.SetCookie));
            });

            var cookie = new CookieInterceptor(jar);
            var redirect = new RedirectInterceptor();
            var interceptors = order == Order.RedirectOuterCookieInner
                ? new IHttpInterceptor[] { redirect, cookie }
                : new IHttpInterceptor[] { cookie, redirect };

            var pipeline = new TestInterceptorPipeline(interceptors, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(startUri));
            var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        private static UHttpResponse Respond(UHttpRequest req, RequestContext ctx, string location, string setCookie)
        {
            var headers = new HttpHeaders();
            if (setCookie != null)
                headers.Add("Set-Cookie", setCookie);

            if (location == null)
                return new UHttpResponse(HttpStatusCode.OK, headers, Array.Empty<byte>(), ctx.Elapsed, req);

            headers.Set("Location", location);
            return new UHttpResponse(HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req);
        }

        private static string Header(CookieJar jar, string uri)
        {
            return jar.GetCookieHeader(new Uri(uri), HttpMethod.GET, isCrossSiteRequest: false);
        }
    }
}
