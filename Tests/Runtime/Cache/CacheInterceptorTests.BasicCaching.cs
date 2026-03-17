using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_CachesSuccessfulGetRequests()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data"));

                var response1 = await pipeline.ExecuteAsync(request, new RequestContext(request));
                var response2 = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response1.StatusCode);
                Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", response2.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", response2.GetBodyAsString());
                Assert.AreEqual(1, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_CacheHit_SetsAgeHeader()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/age"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var cached = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
                Assert.IsTrue(int.TryParse(cached.Headers.Get("Age"), out var ageSeconds));
                Assert.GreaterOrEqual(ageSeconds, 0);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_CacheHit_UsesIndependentBodySnapshot()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var upstreamBody = Encoding.UTF8.GetBytes("payload");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    upstreamBody);

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/zerocopy"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                upstreamBody[0] = (byte)'X';

                var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", hit.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RespectsNoStore()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "no-store");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-store"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual(0, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RevalidatesWithETag_On304()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/revalidate"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual(HttpStatusCode.OK, revalidated.StatusCode);
                Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", revalidated.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RevalidationFailure_RestoresOriginalRequestContext()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;

                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "no-cache");
                    headers.Set("ETag", "\"restore-v1\"");

                    if (callCount == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("\"restore-v1\"", req.Headers.Get("If-None-Match"));
                    throw new UHttpException(new UHttpError(
                        UHttpErrorType.NetworkError,
                        "revalidation failed"));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/revalidate-restore-context");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));

                var secondRequest = new UHttpRequest(HttpMethod.GET, uri);
                var secondContext = new RequestContext(secondRequest);
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(secondRequest, secondContext));

                Assert.AreEqual(UHttpErrorType.NetworkError, ex.HttpError.Type);
                Assert.AreSame(secondRequest, secondContext.Request);
                Assert.IsFalse(secondContext.Request.Headers.Contains("If-None-Match"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RequestNoCache_ForcesRevalidationOfFreshEntry()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
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
        public void CacheInterceptor_ResponseNoCacheFieldName_ForcesRevalidation()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-cache-field"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RevalidateModifiedWithNoStore_EvictsOldEntry()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    callCount++;
                    var headers = new HttpHeaders();

                    if (callCount == 1)
                    {
                        headers.Set("Cache-Control", "max-age=120");
                        headers.Set("ETag", "\"v1\"");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("v1"),
                            ctx.Elapsed,
                            req));
                    }

                    if (callCount == 2)
                    {
                        Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                        headers.Set("Cache-Control", "no-store");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("v2"),
                            ctx.Elapsed,
                            req));
                    }

                    headers.Set("Cache-Control", "max-age=120");
                    headers.Set("ETag", "\"v3\"");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("v3"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/revalidate-modified-no-store");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                var first = await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));
                Assert.AreEqual("v1", first.GetBodyAsString());

                var secondHeaders = new HttpHeaders();
                secondHeaders.Set("Cache-Control", "no-cache");
                var secondRequest = new UHttpRequest(HttpMethod.GET, uri, secondHeaders);
                var second = await pipeline.ExecuteAsync(secondRequest, new RequestContext(secondRequest));
                Assert.AreEqual("v2", second.GetBodyAsString());

                var thirdRequest = new UHttpRequest(HttpMethod.GET, uri);
                var third = await pipeline.ExecuteAsync(thirdRequest, new RequestContext(thirdRequest));
                Assert.AreEqual("v3", third.GetBodyAsString());

                Assert.AreEqual(3, callCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_Revalidation304_MergesAllResponseHeaders()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/merge-headers"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                Assert.AreEqual("initial", second.Headers.Get("X-Origin"));
                Assert.AreEqual("applied", second.Headers.Get("X-Revalidated"));
            }).GetAwaiter().GetResult();
        }
    }
}
