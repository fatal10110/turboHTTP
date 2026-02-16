using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    [TestFixture]
    public class CacheMiddlewareTests
    {
        [Test]
        public void CacheMiddleware_CachesSuccessfulGetRequests()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"));

                var response1 = await pipeline.ExecuteAsync(request, new RequestContext(request));
                var response2 = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response1.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", response2.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(response2.Body.Span));
                Assert.AreEqual(1, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_CacheHit_SetsAgeHeader()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/age"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var cached = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
                Assert.IsTrue(int.TryParse(cached.Headers.Get("Age"), out var ageSeconds));
                Assert.GreaterOrEqual(ageSeconds, 0);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_CacheHit_UsesIndependentBodySnapshot()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var upstreamBody = Encoding.UTF8.GetBytes("payload");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    upstreamBody);

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/zerocopy"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                upstreamBody[0] = (byte)'X';

                var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(hit.Body.Span));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_RespectsNoStore()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "no-store");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-store"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual(0, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_RevalidatesWithETag_On304()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                int callCount = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;

                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "no-cache");
                    headers.Set("ETag", "\"v1\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/revalidate"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual(HttpStatusCode.OK, revalidated.StatusCode);
                Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(revalidated.Body.Span));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_RequestNoCache_ForcesRevalidationOfFreshEntry()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;

                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=120");
                    headers.Set("ETag", "\"fresh-v1\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"fresh-v1\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/request-no-cache");
                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));

                var secondHeaders = new HttpHeaders();
                secondHeaders.Set("Cache-Control", "no-cache");
                var secondRequest = new UHttpRequest(HttpMethod.GET, uri, secondHeaders);
                var second = await pipeline.ExecuteAsync(secondRequest, new RequestContext(secondRequest));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_ResponseNoCacheFieldName_ForcesRevalidation()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;

                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "no-cache=\"Set-Cookie\"");
                    headers.Set("ETag", "\"field-no-cache\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"field-no-cache\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-cache-field"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_Revalidation304_MergesAllResponseHeaders()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        var initialHeaders = new HttpHeaders();
                        initialHeaders.Set("Cache-Control", "no-cache");
                        initialHeaders.Set("ETag", "\"merge-v1\"");
                        initialHeaders.Set("X-Origin", "initial");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            initialHeaders,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    var notModifiedHeaders = new HttpHeaders();
                    notModifiedHeaders.Set("ETag", "\"merge-v1\"");
                    notModifiedHeaders.Set("X-Revalidated", "applied");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        notModifiedHeaders,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/merge-headers"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                Assert.AreEqual("initial", second.Headers.Get("X-Origin"));
                Assert.AreEqual("applied", second.Headers.Get("X-Revalidated"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_NormalizesQueryParameterOrder()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data?b=2&a=1"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data?a=1&b=2"));

                await pipeline.ExecuteAsync(requestA, new RequestContext(requestA));
                var cached = await pipeline.ExecuteAsync(requestB, new RequestContext(requestB));

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_SeparatesAcceptEncodingVariants()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Vary", "Accept-Encoding");

                    var acceptEncoding = req.Headers.Get("Accept-Encoding");
                    var body = string.Equals(acceptEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.UTF8.GetBytes("gzip-body")
                        : Encoding.UTF8.GetBytes("identity-body");

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        body,
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var gzipHeaders = new HttpHeaders();
                gzipHeaders.Set("Accept-Encoding", "gzip");

                var gzipRequestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"), gzipHeaders);
                var identityRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"));
                var gzipRequestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"), gzipHeaders);

                await pipeline.ExecuteAsync(gzipRequestA, new RequestContext(gzipRequestA));
                await pipeline.ExecuteAsync(identityRequest, new RequestContext(identityRequest));
                var gzipCached = await pipeline.ExecuteAsync(gzipRequestB, new RequestContext(gzipRequestB));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual("HIT", gzipCached.Headers.Get("X-Cache"));
                Assert.AreEqual("gzip-body", Encoding.UTF8.GetString(gzipCached.Body.Span));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_StripsHopByHopHeadersBeforeStorage()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Connection", "keep-alive, X-Hop");
                    headers.Set("Keep-Alive", "timeout=30");
                    headers.Set("Transfer-Encoding", "chunked");
                    headers.Set("X-Hop", "volatile");
                    headers.Set("X-Stable", "persisted");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("payload"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/hop-strip"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
                Assert.IsFalse(second.Headers.Contains("Connection"));
                Assert.IsFalse(second.Headers.Contains("Keep-Alive"));
                Assert.IsFalse(second.Headers.Contains("Transfer-Encoding"));
                Assert.IsFalse(second.Headers.Contains("X-Hop"));
                Assert.AreEqual("persisted", second.Headers.Get("X-Stable"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_DoesNotStore_WhenVaryHeaderCountExceedsLimit()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var varyBuilder = new StringBuilder();
                for (int i = 0; i < 40; i++)
                {
                    if (i > 0)
                        varyBuilder.Append(", ");
                    varyBuilder.Append("x-vary-");
                    varyBuilder.Append(i);
                }

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Vary", varyBuilder.ToString());
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("payload"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/vary-limit"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.IsNull(second.Headers.Get("X-Cache"));
                Assert.AreEqual(0, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_InvalidatesOnUnsafeMethodForSameUri()
        {
            Task.Run(async () =>
            {
                int getVersion = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Method == HttpMethod.GET)
                    {
                        getVersion++;
                        var getHeaders = new HttpHeaders();
                        getHeaders.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            getHeaders,
                            Encoding.UTF8.GetBytes("v" + getVersion),
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var uri = new Uri("https://example.test/profile");
                var getRequest = new UHttpRequest(HttpMethod.GET, uri);
                var postRequest = new UHttpRequest(HttpMethod.POST, uri, body: Encoding.UTF8.GetBytes("update"));

                var first = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));
                var second = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));
                await pipeline.ExecuteAsync(postRequest, new RequestContext(postRequest));
                var third = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));

                Assert.AreEqual("v1", Encoding.UTF8.GetString(first.Body.Span));
                Assert.AreEqual("v1", Encoding.UTF8.GetString(second.Body.Span));
                Assert.AreEqual("v2", Encoding.UTF8.GetString(third.Body.Span));
                Assert.AreEqual(3, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_UnsafeInvalidation_AlsoInvalidatesLocationAndContentLocation()
        {
            Task.Run(async () =>
            {
                int profileVersion = 0;
                int summaryVersion = 0;

                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Method == HttpMethod.GET && req.Uri.AbsolutePath == "/profile")
                    {
                        profileVersion++;
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("profile-v" + profileVersion),
                            ctx.Elapsed,
                            req));
                    }

                    if (req.Method == HttpMethod.GET && req.Uri.AbsolutePath == "/summary")
                    {
                        summaryVersion++;
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("summary-v" + summaryVersion),
                            ctx.Elapsed,
                            req));
                    }

                    var postHeaders = new HttpHeaders();
                    postHeaders.Set("Location", "/profile");
                    postHeaders.Set("Content-Location", "/summary");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        postHeaders,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var profileUri = new Uri("https://example.test/profile");
                var summaryUri = new Uri("https://example.test/summary");
                var updateUri = new Uri("https://example.test/update");

                var profileSeed = new UHttpRequest(HttpMethod.GET, profileUri);
                await pipeline.ExecuteAsync(profileSeed, new RequestContext(profileSeed));
                var summarySeed = new UHttpRequest(HttpMethod.GET, summaryUri);
                await pipeline.ExecuteAsync(summarySeed, new RequestContext(summarySeed));

                var profileCached = new UHttpRequest(HttpMethod.GET, profileUri);
                await pipeline.ExecuteAsync(profileCached, new RequestContext(profileCached));
                var summaryCached = new UHttpRequest(HttpMethod.GET, summaryUri);
                await pipeline.ExecuteAsync(summaryCached, new RequestContext(summaryCached));

                var updateRequest = new UHttpRequest(HttpMethod.POST, updateUri, body: Encoding.UTF8.GetBytes("update"));
                await pipeline.ExecuteAsync(updateRequest, new RequestContext(updateRequest));

                var profileAfterRequest = new UHttpRequest(HttpMethod.GET, profileUri);
                var profileAfter = await pipeline.ExecuteAsync(profileAfterRequest, new RequestContext(profileAfterRequest));
                var summaryAfterRequest = new UHttpRequest(HttpMethod.GET, summaryUri);
                var summaryAfter = await pipeline.ExecuteAsync(summaryAfterRequest, new RequestContext(summaryAfterRequest));

                Assert.AreEqual("profile-v2", Encoding.UTF8.GetString(profileAfter.Body.Span));
                Assert.AreEqual("summary-v2", Encoding.UTF8.GetString(summaryAfter.Body.Span));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_RevalidatesExpiredEntries_ThatHaveValidators()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=0");
                    headers.Set("ETag", "\"v1\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expired-revalidate"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", Encoding.UTF8.GetString(second.Body.Span));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_CachesRfc9111DefaultCacheable404Responses()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.NotFound,
                    headers,
                    Encoding.UTF8.GetBytes("missing"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/not-found"));

                var first = await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.NotFound, first.StatusCode);
                Assert.AreEqual(HttpStatusCode.NotFound, second.StatusCode);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_IgnoresSMaxAgeForPrivateCache()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=120, s-maxage=0");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/s-maxage"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_SubtractsUpstreamAgeFromFreshnessLifetime()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=120");
                    headers.Set("Age", "120");
                    headers.Set("ETag", "\"aged\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"aged\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/upstream-age"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_TreatsExpiresZeroAsExpiredImmediately()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var headers = new HttpHeaders();
                    headers.Set("Expires", "0");
                    headers.Set("ETag", "\"expires-zero\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"expires-zero\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expires-zero"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_AuthorizedRequest_CachesWhenResponseIsPublic()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy
                {
                    Storage = storage,
                    AllowCacheForAuthorizedRequests = false
                });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "public, max-age=60");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("auth-cacheable"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var requestHeaders = new HttpHeaders();
                requestHeaders.Set("Authorization", "Bearer token");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/auth-public"), requestHeaders);

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_DoesNotReorderDuplicateQueryParameters()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes(req.Uri.Query),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/dup?a=2&a=1"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/dup?a=1&a=2"));

                await pipeline.ExecuteAsync(requestA, new RequestContext(requestA));
                var second = await pipeline.ExecuteAsync(requestB, new RequestContext(requestB));

                Assert.AreEqual(2, callCount);
                Assert.IsNull(second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_HandlesConcurrentRequests_WithoutCorruptingCache()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheMiddleware(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("payload"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new HttpPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/contention");
                var tasks = new List<Task<UHttpResponse>>();
                for (int i = 0; i < 20; i++)
                {
                    var request = new UHttpRequest(HttpMethod.GET, uri);
                    tasks.Add(pipeline.ExecuteAsync(request, new RequestContext(request)));
                }

                var responses = await Task.WhenAll(tasks);
                for (int i = 0; i < responses.Length; i++)
                {
                    Assert.AreEqual(HttpStatusCode.OK, responses[i].StatusCode);
                    Assert.AreEqual("payload", Encoding.UTF8.GetString(responses[i].Body.Span));
                }

                var finalRequest = new UHttpRequest(HttpMethod.GET, uri);
                var cached = await pipeline.ExecuteAsync(finalRequest, new RequestContext(finalRequest));
                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheMiddleware_ImplementsIDisposable()
        {
            Assert.IsTrue(new CacheMiddleware() is IDisposable);
        }
    }
}
