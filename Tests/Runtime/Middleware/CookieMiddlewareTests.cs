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
    [TestFixture]
    public class CookieMiddlewareTests
    {
        [Test]
        public void CookieMiddleware_AttachesMatchingCookies()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;

                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "session=abc; Path=/; HttpOnly");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    StringAssert.Contains("session=abc", req.Headers.Get("Cookie"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/login"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/profile"));

                await pipeline.ExecuteAsync(first, new RequestContext(first));
                await pipeline.ExecuteAsync(second, new RequestContext(second));

                Assert.AreEqual(2, callCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieMiddleware_ExcludesDomainAndPathMismatches()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "api=1; Domain=example.test; Path=/api");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.IsNull(req.Headers.Get("Cookie"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/api/login"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/other"));

                await pipeline.ExecuteAsync(first, new RequestContext(first));
                await pipeline.ExecuteAsync(second, new RequestContext(second));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieMiddleware_DoesNotSendExpiredCookies()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "session=gone; Path=/; Max-Age=0");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.IsNull(req.Headers.Get("Cookie"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/login"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/profile"));

                await pipeline.ExecuteAsync(first, new RequestContext(first));
                await pipeline.ExecuteAsync(second, new RequestContext(second));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieJar_MaxAgeTakesPrecedenceOverExpires()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/login"),
                new[] { "a=b; Max-Age=3600; Expires=Thu, 01 Jan 1970 00:00:00 GMT; Path=/" },
                utcNowOverride: now);

            var beforeExpiry = jar.GetCookieHeader(
                new Uri("https://example.test/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddMinutes(30));
            Assert.AreEqual("a=b", beforeExpiry);

            var afterExpiry = jar.GetCookieHeader(
                new Uri("https://example.test/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddHours(2));
            Assert.IsNull(afterExpiry);
        }

        [Test]
        public void CookieMiddleware_HonorsSecureAttribute()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "sid=secure; Path=/; Secure");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.IsNull(req.Headers.Get("Cookie"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/login"));
                var second = new UHttpRequest(HttpMethod.GET, new Uri("http://example.test/profile"));

                await pipeline.ExecuteAsync(first, new RequestContext(first));
                await pipeline.ExecuteAsync(second, new RequestContext(second));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieJar_RejectsSecureCookiesFromHttpOrigins()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("http://example.test/login"),
                new[] { "sid=secure; Path=/; Secure" },
                utcNowOverride: now);

            var secureHeader = jar.GetCookieHeader(
                new Uri("https://example.test/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));

            Assert.IsNull(secureHeader);
            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void CookieJar_EnforcesDomainAndTotalLimits()
        {
            var jar = new CookieJar(maxCookiesPerDomain: 2, maxTotalCookies: 3);

            var baseTime = DateTime.UtcNow;
            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/"),
                new[]
                {
                    "c1=1; Path=/",
                    "c2=2; Path=/",
                    "c3=3; Path=/"
                },
                utcNowOverride: baseTime);

            Assert.LessOrEqual(jar.GetDomainCount("example.test"), 2);
            var exampleCookies = jar.GetCookieHeader(
                new Uri("https://example.test/"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: baseTime.AddSeconds(1));
            Assert.IsFalse(exampleCookies != null && exampleCookies.Contains("c1=1"));

            jar.StoreFromSetCookieHeaders(
                new Uri("https://other.test/"),
                new[]
                {
                    "o1=1; Path=/",
                    "o2=2; Path=/"
                },
                utcNowOverride: baseTime.AddSeconds(2));

            Assert.LessOrEqual(jar.Count, 3);
        }

        [Test]
        public void CookieJar_ReadPath_SweepsExpiredCookies()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/login"),
                new[] { "sid=1; Path=/; Max-Age=1" },
                utcNowOverride: now);

            Assert.AreEqual(1, jar.Count);

            var header = jar.GetCookieHeader(
                new Uri("https://example.test/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(2));

            Assert.IsNull(header);
            Assert.AreEqual(0, jar.Count);
        }

        [Test]
        public void CookieJar_UpdatesLastAccessedUtc_ForLruEviction()
        {
            var jar = new CookieJar(maxCookiesPerDomain: 2, maxTotalCookies: 2);
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/a/login"),
                new[]
                {
                    "a=1; Path=/a",
                    "b=2; Path=/b"
                },
                utcNowOverride: now);

            var touched = jar.GetCookieHeader(
                new Uri("https://example.test/a/resource"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));
            StringAssert.Contains("a=1", touched);

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/c/login"),
                new[] { "c=3; Path=/c" },
                utcNowOverride: now.AddSeconds(2));

            var aHeader = jar.GetCookieHeader(
                new Uri("https://example.test/a/next"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(3));
            StringAssert.Contains("a=1", aHeader);

            var bHeader = jar.GetCookieHeader(
                new Uri("https://example.test/b/next"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(3));
            Assert.IsNull(bHeader);

            var cHeader = jar.GetCookieHeader(
                new Uri("https://example.test/c/next"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(3));
            StringAssert.Contains("c=3", cHeader);
        }

        [Test]
        public void CookieMiddleware_RespectsSameSiteForCrossSiteRequests()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "sid=1; Path=/; SameSite=Strict");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.IsNull(req.Headers.Get("Cookie"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/login"));
                await pipeline.ExecuteAsync(first, new RequestContext(first));

                var metadata = new Dictionary<string, object>
                {
                    [RequestMetadataKeys.IsCrossSiteRequest] = true
                };
                var second = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://example.test/resource"),
                    metadata: metadata);
                await pipeline.ExecuteAsync(second, new RequestContext(second));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieJar_DefaultPath_ExcludesTrailingSlash()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/app/login"),
                new[] { "sid=1" },
                utcNowOverride: now);

            var atParent = jar.GetCookieHeader(
                new Uri("https://example.test/app"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));
            var atSibling = jar.GetCookieHeader(
                new Uri("https://example.test/app/other"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));

            StringAssert.Contains("sid=1", atParent);
            StringAssert.Contains("sid=1", atSibling);
        }

        [Test]
        public void CookieJar_RejectsSingleLabelDomainAttribute()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.com/login"),
                new[] { "sid=1; Domain=com; Path=/" },
                utcNowOverride: now);

            var header = jar.GetCookieHeader(
                new Uri("https://example.com/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));

            Assert.IsNull(header);
        }

        [Test]
        public void CookieJar_RejectsKnownMultiLabelPublicSuffixDomainAttribute()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.co.uk/login"),
                new[] { "sid=1; Domain=co.uk; Path=/" },
                utcNowOverride: now);

            var header = jar.GetCookieHeader(
                new Uri("https://example.co.uk/profile"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));

            Assert.IsNull(header);
        }

        [Test]
        public void CookieJar_UnquotesCookieValues()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/"),
                new[] { "token=\"abc\"; Path=/" },
                utcNowOverride: now);

            var header = jar.GetCookieHeader(
                new Uri("https://example.test/resource"),
                HttpMethod.GET,
                isCrossSiteRequest: false,
                utcNowOverride: now.AddSeconds(1));

            Assert.AreEqual("token=abc", header);
        }

        [Test]
        public void CookieJar_FiltersSameSiteByMethodAndCrossSiteMode()
        {
            var jar = new CookieJar();
            var now = DateTime.UtcNow;

            jar.StoreFromSetCookieHeaders(
                new Uri("https://example.test/login"),
                new[]
                {
                    "strict_cookie=1; Path=/; SameSite=Strict",
                    "lax_cookie=1; Path=/; SameSite=Lax",
                    "none_cookie=1; Path=/; SameSite=None"
                },
                utcNowOverride: now);

            var crossSiteGet = jar.GetCookieHeader(
                new Uri("https://example.test/data"),
                HttpMethod.GET,
                isCrossSiteRequest: true,
                utcNowOverride: now.AddSeconds(1));
            Assert.IsFalse(crossSiteGet.Contains("strict_cookie=1"));
            StringAssert.Contains("lax_cookie=1", crossSiteGet);
            StringAssert.Contains("none_cookie=1", crossSiteGet);

            var crossSitePost = jar.GetCookieHeader(
                new Uri("https://example.test/data"),
                HttpMethod.POST,
                isCrossSiteRequest: true,
                utcNowOverride: now.AddSeconds(2));
            Assert.IsFalse(crossSitePost.Contains("strict_cookie=1"));
            Assert.IsFalse(crossSitePost.Contains("lax_cookie=1"));
            StringAssert.Contains("none_cookie=1", crossSitePost);
        }

        [Test]
        public void CookieMiddleware_DoesNotDuplicateCookieNamesWhenMergingExistingHeader()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Add("Set-Cookie", "sid=jar; Path=/");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    var cookieHeader = req.Headers.Get("Cookie");
                    Assert.IsNotNull(cookieHeader);
                    StringAssert.Contains("sid=manual", cookieHeader);
                    StringAssert.DoesNotContain("sid=jar", cookieHeader);
                    StringAssert.Contains("theme=dark", cookieHeader);

                    var tokens = cookieHeader.Split(';');
                    int sidCount = 0;
                    for (int i = 0; i < tokens.Length; i++)
                    {
                        if (tokens[i].TrimStart().StartsWith("sid=", StringComparison.Ordinal))
                            sidCount++;
                    }

                    Assert.AreEqual(1, sidCount);

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new CookieMiddleware(new CookieJar());
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var first = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/login"));
                await pipeline.ExecuteAsync(first, new RequestContext(first));

                var secondHeaders = new HttpHeaders();
                secondHeaders.Set("Cookie", "sid=manual; theme=dark");
                var second = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/profile"), secondHeaders);
                await pipeline.ExecuteAsync(second, new RequestContext(second));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CookieMiddleware_ImplementsIDisposable()
        {
            Assert.IsTrue(new CookieMiddleware() is IDisposable);
        }
    }
}
