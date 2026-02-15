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
    }
}
