using System;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_VariantIndex_StaysBoundedByMemoryStorageCapacity()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage(maxEntries: 100);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("x"));
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                for (int i = 0; i < 10000; i++)
                {
                    var request = new UHttpRequest(
                        HttpMethod.GET,
                        new Uri("https://example.test/item/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                }

                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(30),
                    "Background cache stores did not drain.");

                Assert.AreEqual(10000, transport.RequestCount);
                Assert.LessOrEqual(middleware.VariantIndexCount, 100);
                Assert.LessOrEqual(await storage.GetCountAsync(), 100);
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_VariantIndex_EvictedVaryVariantIsRemovedFromStorage()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage(maxEntries: 1000);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                middleware.VariantIndexCapacity = 2;

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var responseHeaders = new HttpHeaders();
                    responseHeaders.Set("Cache-Control", "max-age=60");
                    responseHeaders.Set("Vary", "Accept");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        responseHeaders,
                        Encoding.UTF8.GetBytes("payload"),
                        TimeSpan.Zero,
                        req));
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                foreach (var path in new[] { "/a", "/b", "/c" })
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test" + path));
                    request.Headers.Set("Accept", "text/plain");
                    await pipeline.ExecuteAsync(request, new RequestContext(request));
                }

                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");

                // The index dropped /a; its Vary entry is unreachable through the index (and
                // invalidation could no longer find it), so it must be removed from storage too.
                Assert.AreEqual(2, middleware.VariantIndexCount);
                Assert.AreEqual(2, await storage.GetCountAsync());
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_Lifecycle_DisposeReleasesStoreQueuedBehindPendingMutation()
        {
            Task.Run(async () =>
            {
                var storage = new DelayedSetCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var uri = new Uri("https://example.test/held");
                const string baseKey = "GET https://example.test/held";
                var responseHeaders = new HttpHeaders();
                responseHeaders.Set("Cache-Control", "max-age=60");

                var first = new SegmentedBuffer();
                first.Write(Encoding.UTF8.GetBytes("first"));
                var second = new SegmentedBuffer();
                second.Write(Encoding.UTF8.GetBytes("second"));

                middleware.QueueStoreResponse(HttpMethod.GET, uri, new HttpHeaders(), baseKey, 200, responseHeaders, first);
                await WaitUntilAsync(
                    () => Task.FromResult(storage.SetStarted.IsSet),
                    TimeSpan.FromSeconds(10),
                    "First store did not reach storage.");

                // Queued behind the held store of the same base key.
                middleware.QueueStoreResponse(HttpMethod.GET, uri, new HttpHeaders(), baseKey, 200, responseHeaders, second);
                var token = middleware.BackgroundWorkToken;

                middleware.Dispose();

                await WaitUntilAsync(
                    () => Task.FromResult(IsSegmentedBufferDisposed(first) && IsSegmentedBufferDisposed(second)),
                    TimeSpan.FromSeconds(10),
                    "A pooled store body was not returned after Dispose.");
                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Pending mutations did not drain after Dispose.");

                Assert.IsTrue(token.IsCancellationRequested);
                Assert.Throws<ObjectDisposedException>(() => { var _ = token.WaitHandle; });
                storage.Dispose();
            }).GetAwaiter().GetResult();
        }

        private static bool IsSegmentedBufferDisposed(SegmentedBuffer buffer)
        {
            try
            {
                buffer.AsSequence();
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
