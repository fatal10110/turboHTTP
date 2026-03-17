using System;
using System.Collections.Generic;
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
        public void CacheInterceptor_CachesRfc9111DefaultCacheable404Responses()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.NotFound,
                    headers,
                    Encoding.UTF8.GetBytes("missing"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
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
        public void CacheInterceptor_IgnoresSMaxAgeForPrivateCache()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=120, s-maxage=0");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/s-maxage"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_SubtractsUpstreamAgeFromFreshnessLifetime()
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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/upstream-age"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_TreatsExpiresZeroAsExpiredImmediately()
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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expires-zero"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_AuthorizedRequest_CachesWhenResponseIsPublic()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy
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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
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
        public void CacheInterceptor_DoesNotReorderDuplicateQueryParameters()
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
                    headers.Set("Cache-Control", "max-age=60");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes(req.Uri.Query),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/dup?a=2&a=1"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/dup?a=1&a=2"));

                await pipeline.ExecuteAsync(requestA, new RequestContext(requestA));
                var second = await pipeline.ExecuteAsync(requestB, new RequestContext(requestB));

                Assert.AreEqual(2, callCount);
                Assert.IsNull(second.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_HandlesConcurrentRequests_WithoutCorruptingCache()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

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

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
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
                    Assert.AreEqual("payload", responses[i].GetBodyAsString());
                }

                var finalRequest = new UHttpRequest(HttpMethod.GET, uri);
                var cached = await pipeline.ExecuteAsync(finalRequest, new RequestContext(finalRequest));
                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_ImplementsIDisposable()
        {
            Assert.IsTrue(new CacheInterceptor() is IDisposable);
        }
    }
}
