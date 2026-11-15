using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    // #469: Vary variants per base key are bounded (RFC 9111 section 4.1 sets no limit; CLAUDE.md
    // requires every peer-controlled size to be bounded).
    public partial class CacheInterceptorTests
    {
        private static readonly Uri VaryBoundUri = new Uri("https://example.test/vary-bound");

        [Test]
        public void CacheInterceptor_VaryVariants_PerRequestHeaderValue_StaysBoundedPerBaseKey()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage(maxEntries: 20000, maxSizeBytes: 512L * 1024 * 1024);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var transport = CreateVaryBoundTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                for (int i = 0; i < 10000; i++)
                    await SendVaryBoundRequestAsync(pipeline, "id-" + i.ToString(CultureInfo.InvariantCulture));

                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(30),
                    "Background cache mutations did not drain.");

                Assert.AreEqual(10000, transport.RequestCount);
                Assert.LessOrEqual(
                    middleware.GetVariantCount(HttpMethod.GET, VaryBoundUri),
                    CacheInterceptor.DefaultMaxVariantsPerBaseKey);
                // Evicted variants leave storage too, not only the index.
                Assert.LessOrEqual(await storage.GetCountAsync(), CacheInterceptor.DefaultMaxVariantsPerBaseKey);
                Assert.AreEqual(1, middleware.VariantIndexCount);
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_VaryVariants_OverTheBound_EvictsTheOldestVariantFromStorage()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage(maxEntries: 1000);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                middleware.MaxVariantsPerBaseKey = 2;
                var transport = CreateVaryBoundTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                foreach (var id in new[] { "a", "b", "c" })
                    await SendVaryBoundRequestAsync(pipeline, id);
                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");

                Assert.AreEqual(2, middleware.GetVariantCount(HttpMethod.GET, VaryBoundUri));
                Assert.AreEqual(2, await storage.GetCountAsync());

                var newest = await SendVaryBoundRequestAsync(pipeline, "c");
                Assert.AreEqual("HIT", newest.Headers.Get("X-Cache"));
                Assert.AreEqual(3, transport.RequestCount);

                var oldest = await SendVaryBoundRequestAsync(pipeline, "a");
                Assert.AreNotEqual("HIT", oldest.Headers.Get("X-Cache"));
                Assert.AreEqual(4, transport.RequestCount, "The oldest variant was evicted and refetched.");
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_VaryVariants_RepeatedEvictions_RefuseNewVariantsButKeepExistingOnes()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage(maxEntries: 1000);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                middleware.MaxVariantsPerBaseKey = 2;
                var transport = CreateVaryBoundTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                // v0 and v1 fill the bucket, v2 and v3 each evict one (2 evictions = the bound),
                // so v4 and v5 are refused.
                for (int i = 0; i < 6; i++)
                {
                    await SendVaryBoundRequestAsync(pipeline, "v" + i.ToString(CultureInfo.InvariantCulture));
                    await WaitUntilAsync(
                        () => Task.FromResult(middleware.PendingMutationCount == 0),
                        TimeSpan.FromSeconds(10),
                        "Background cache mutations did not drain.");
                }

                Assert.AreEqual(2, middleware.GetVariantCount(HttpMethod.GET, VaryBoundUri));
                Assert.AreEqual(2, await storage.GetCountAsync());

                var kept = await SendVaryBoundRequestAsync(pipeline, "v3");
                Assert.AreEqual("HIT", kept.Headers.Get("X-Cache"));
                Assert.AreEqual(6, transport.RequestCount);

                var refused = await SendVaryBoundRequestAsync(pipeline, "v5");
                Assert.AreNotEqual("HIT", refused.Headers.Get("X-Cache"));
                Assert.AreEqual(7, transport.RequestCount, "A refused variant was never stored.");
                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");
                Assert.AreEqual(2, await storage.GetCountAsync());
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        private static MockTransport CreateVaryBoundTransport()
        {
            return new MockTransport((req, ctx, ct) =>
            {
                var responseHeaders = new HttpHeaders();
                responseHeaders.Set("Cache-Control", "max-age=60");
                responseHeaders.Set("Vary", "X-Request-Id");
                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK,
                    responseHeaders,
                    Encoding.UTF8.GetBytes("payload"),
                    TimeSpan.Zero,
                    req));
            });
        }

        private static Task<UHttpResponse> SendVaryBoundRequestAsync(TestInterceptorPipeline pipeline, string requestId)
        {
            var request = new UHttpRequest(HttpMethod.GET, VaryBoundUri);
            request.Headers.Set("X-Request-Id", requestId);
            return pipeline.ExecuteAsync(request, new RequestContext(request));
        }
    }
}
