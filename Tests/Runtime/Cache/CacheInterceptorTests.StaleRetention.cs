using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    // #500: MemoryCacheStorage never evicts on freshness; a stale entry with a validator stays for
    // conditional revalidation (RFC 9111 section 4.3) until the entry/size bounds push it out.
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_StaleEntryWithValidator_SurvivesLaterStoresAndIsRevalidated()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                // A fixed past instant: the stored entry is stale for the interceptor's clock and also
                // for the real clock a storage-side sweep used to read.
                var now = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                middleware.UtcNowProvider = () => now;

                int validatedCalls = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    var status = HttpStatusCode.OK;
                    var body = Encoding.UTF8.GetBytes("payload");
                    if (req.Uri.AbsolutePath == "/validated")
                    {
                        headers.Set("ETag", "\"v1\"");
                        if (validatedCalls++ > 0)
                        {
                            Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                            status = HttpStatusCode.NotModified;
                            body = Array.Empty<byte>();
                        }
                    }

                    return Task.FromResult(new UHttpResponse(status, headers, body, TimeSpan.Zero, req));
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var validated = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/validated"));
                var other = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/other"));

                await pipeline.ExecuteAsync(validated, new RequestContext(validated));
                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");

                // Past max-age and past the 15 s sweep interval storage used to have.
                now = now.AddMinutes(5);
                await pipeline.ExecuteAsync(other, new RequestContext(other));
                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");

                Assert.AreEqual(2, await storage.GetCountAsync(), "No entry is evicted on freshness.");

                var revalidated = await pipeline.ExecuteAsync(validated, new RequestContext(validated));
                Assert.AreEqual(2, validatedCalls);
                Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", revalidated.GetBodyAsString());
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void MemoryCacheStorage_ExpiredEntries_AreKeptUntilTheBoundsEvictThem()
        {
            Task.Run(async () =>
            {
                using var storage = new MemoryCacheStorage(maxEntries: 4);
                var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

                // 'no-cache + validator' (no expiry) and 'max-age=0 + validator' (expired) are kept alike.
                await storage.SetAsync("no-cache-etag", CreateStoredEntry("no-cache-etag", null, "\"a\"", null, mustRevalidate: true));
                await storage.SetAsync("max-age-0-etag", CreateStoredEntry("max-age-0-etag", past, "\"b\"", null, mustRevalidate: false));
                await storage.SetAsync("expired-last-modified", CreateStoredEntry("expired-last-modified", past, null, "Wed, 01 Jan 2020 00:00:00 GMT", mustRevalidate: false));
                await storage.SetAsync("fresh", CreateStoredEntry("fresh", DateTime.UtcNow.AddHours(1), null, null, mustRevalidate: false));

                Assert.AreEqual(4, await storage.GetCountAsync());
                Assert.IsNotNull(await storage.GetAsync("max-age-0-etag"));
                Assert.IsNotNull(await storage.GetAsync("expired-last-modified"));
                Assert.IsNotNull(await storage.GetAsync("fresh"));

                // At the entry bound the least recently used entry goes, not the expired ones.
                await storage.SetAsync("fifth", CreateStoredEntry("fifth", past, null, null, mustRevalidate: false));
                Assert.AreEqual(4, await storage.GetCountAsync());
                Assert.IsNull(await storage.GetAsync("no-cache-etag"));
                Assert.IsNotNull(await storage.GetAsync("max-age-0-etag"));
                Assert.IsNotNull(await storage.GetAsync("fifth"));
            }).GetAwaiter().GetResult();
        }

        private static CacheEntry CreateStoredEntry(
            string key,
            DateTime? expiresAtUtc,
            string eTag,
            string lastModified,
            bool mustRevalidate)
        {
            return new CacheEntry(
                key: key,
                statusCode: HttpStatusCode.OK,
                headers: new HttpHeaders(),
                body: Encoding.UTF8.GetBytes("payload"),
                cachedAtUtc: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                expiresAtUtc: expiresAtUtc,
                eTag: eTag,
                lastModified: lastModified,
                responseUrl: new Uri("https://example.test/" + key),
                varyHeaders: null,
                varyKey: null,
                staleWhileRevalidate: null,
                mustRevalidate: mustRevalidate);
        }
    }
}
