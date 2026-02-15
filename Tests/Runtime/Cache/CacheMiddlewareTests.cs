using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.InteropServices;
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
        public void CacheMiddleware_CacheHit_ReusesBodyBuffer()
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
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/zerocopy"));

                var miss = await pipeline.ExecuteAsync(request, new RequestContext(request));
                var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.IsTrue(MemoryMarshal.TryGetArray(miss.Body, out var missSegment));
                Assert.IsTrue(MemoryMarshal.TryGetArray(hit.Body, out var hitSegment));
                Assert.AreSame(missSegment.Array, hitSegment.Array);
                Assert.AreEqual(missSegment.Offset, hitSegment.Offset);
                Assert.AreEqual(missSegment.Count, hitSegment.Count);
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
    }
}
