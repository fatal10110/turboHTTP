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
    /// #212: every redirect hop carries the Cookie header computed for its own request-uri
    /// (RFC 6265 section 5.4), whichever order the Cookie and Redirect interceptors have.
    /// </summary>
    [TestFixture]
    public class CookieRedirectRequestHeaderTests
    {
        public enum Order
        {
            RedirectOuterCookieInner,
            CookieOuterRedirectInner
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void CrossHostHop_CarriesTargetHostCookies_NotTheFirstHosts(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                Seed(jar, "https://a.test/", "a_sid=1; Path=/");
                Seed(jar, "https://b.test/", "b_sid=2; Path=/");

                var sent = await RunChainAsync(order, jar, new Dictionary<string, string>
                {
                    ["https://a.test/start"] = "https://b.test/final",
                    ["https://b.test/final"] = null
                }, "https://a.test/start", callerCookie: null);

                Assert.AreEqual("a_sid=1", sent["https://a.test/start"]);
                Assert.AreEqual("b_sid=2", sent["https://b.test/final"]);
            });
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void SameOriginHopToOtherPath_CarriesTheNewPathsCookies(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                Seed(jar, "https://a.test/", "root=0; Path=/");
                Seed(jar, "https://a.test/", "one=1; Path=/one");
                Seed(jar, "https://a.test/", "two=2; Path=/two");

                var sent = await RunChainAsync(order, jar, new Dictionary<string, string>
                {
                    ["https://a.test/one/start"] = "https://a.test/two/end",
                    ["https://a.test/two/end"] = null
                }, "https://a.test/one/start", callerCookie: null);

                AssertCookies(sent["https://a.test/one/start"], new[] { "root=0", "one=1" }, new[] { "two=2" });
                AssertCookies(sent["https://a.test/two/end"], new[] { "root=0", "two=2" }, new[] { "one=1" });
            });
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void SetCookieOnHop_IsSentOnTheNextSameHostHop(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                var sent = await RunChainAsync(order, jar, new Dictionary<string, string>
                {
                    ["https://a.test/login"] = "https://a.test/home",
                    ["https://a.test/home"] = null
                }, "https://a.test/login", callerCookie: null, setCookieOnFirstHop: "fresh=1; Path=/");

                Assert.IsNull(sent["https://a.test/login"]);
                Assert.AreEqual("fresh=1", sent["https://a.test/home"]);
            });
        }

        [TestCase(Order.RedirectOuterCookieInner)]
        [TestCase(Order.CookieOuterRedirectInner)]
        public void CallerCookie_KeptOnSameOriginHop_DroppedOnCrossOriginHop(Order order)
        {
            AssertAsync.Run(async () =>
            {
                var jar = new CookieJar();
                Seed(jar, "https://a.test/", "one=1; Path=/one");
                Seed(jar, "https://b.test/", "b_sid=2; Path=/");

                var sent = await RunChainAsync(order, jar, new Dictionary<string, string>
                {
                    ["https://a.test/one"] = "https://a.test/two",
                    ["https://a.test/two"] = "https://b.test/end",
                    ["https://b.test/end"] = null
                }, "https://a.test/one", callerCookie: "user=x");

                AssertCookies(sent["https://a.test/one"], new[] { "user=x", "one=1" }, Array.Empty<string>());
                AssertCookies(sent["https://a.test/two"], new[] { "user=x" }, new[] { "one=1" });
                Assert.AreEqual("b_sid=2", sent["https://b.test/end"]);
            });
        }

        private static void Seed(CookieJar jar, string uri, string setCookie)
        {
            jar.StoreFromSetCookieHeaders(new Uri(uri), new[] { setCookie });
        }

        private static void AssertCookies(string header, string[] expected, string[] unexpected)
        {
            Assert.IsNotNull(header);
            var tokens = new HashSet<string>(header.Split(new[] { "; " }, StringSplitOptions.None));
            foreach (var cookie in expected)
                Assert.IsTrue(tokens.Contains(cookie), $"'{cookie}' missing from '{header}'");
            foreach (var cookie in unexpected)
                Assert.IsFalse(tokens.Contains(cookie), $"'{cookie}' unexpectedly in '{header}'");
        }

        private static async Task<Dictionary<string, string>> RunChainAsync(
            Order order,
            CookieJar jar,
            Dictionary<string, string> locations,
            string startUri,
            string callerCookie,
            string setCookieOnFirstHop = null)
        {
            var sent = new Dictionary<string, string>();
            var transport = new MockTransport((req, ctx, ct) =>
            {
                var uri = req.Uri.AbsoluteUri;
                sent[uri] = req.Headers.Get("Cookie");

                var headers = new HttpHeaders();
                if (setCookieOnFirstHop != null && uri == startUri)
                    headers.Add("Set-Cookie", setCookieOnFirstHop);

                var location = locations[uri];
                if (location == null)
                    return Task.FromResult(new UHttpResponse(HttpStatusCode.OK, headers, Array.Empty<byte>(), ctx.Elapsed, req));

                headers.Set("Location", location);
                return Task.FromResult(new UHttpResponse(HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req));
            });

            var cookie = new CookieInterceptor(jar);
            var redirect = new RedirectInterceptor();
            var interceptors = order == Order.RedirectOuterCookieInner
                ? new IHttpInterceptor[] { redirect, cookie }
                : new IHttpInterceptor[] { cookie, redirect };

            var pipeline = new TestInterceptorPipeline(interceptors, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(startUri));
            if (callerCookie != null)
                request.WithHeader("Cookie", callerCookie);

            var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual(locations.Count, sent.Count, "every hop must reach the transport");
            return sent;
        }
    }
}
