using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
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

        [Test]
        public void CacheInterceptor_VaryVariants_EvictionOn304Merge_RemovesEvictedVariantDespiteRequestCancel()
        {
            Task.Run(async () =>
            {
                using var requestCts = new CancellationTokenSource();
                // The third SetAsync is the 304-merged variant, stored on the request token; it evicts "b".
                var storage = new CancelOnSetStorage(requestCts, cancelOnSetNumber: 3);
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                middleware.MaxVariantsPerBaseKey = 2;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"v1\"");
                    if (req.Headers.Get("If-None-Match") != null)
                    {
                        // A new Vary list gives the freshened entry a new signature, so it is a new variant.
                        headers.Set("Cache-Control", "max-age=60");
                        headers.Set("Vary", "X-Request-Id, X-Other");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.NotModified, headers, Array.Empty<byte>(), TimeSpan.Zero, req));
                    }

                    headers.Set("Cache-Control", "max-age=0");
                    headers.Set("Vary", "X-Request-Id");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("payload"), TimeSpan.Zero, req));
                });
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                // "b" is stored first, so it is the least recently stored variant.
                foreach (var id in new[] { "b", "a" })
                {
                    await SendVaryBoundRequestAsync(pipeline, id);
                    await WaitUntilAsync(
                        () => Task.FromResult(middleware.PendingMutationCount == 0),
                        TimeSpan.FromSeconds(10),
                        "Background cache mutations did not drain.");
                }

                Assert.AreEqual(2, storage.SetKeys.Count);
                var evictedKey = storage.SetKeys[0];

                var request = new UHttpRequest(HttpMethod.GET, VaryBoundUri);
                request.Headers.Set("X-Request-Id", "a");
                try
                {
                    await pipeline.ExecuteAsync(request, new RequestContext(request), requestCts.Token);
                }
                catch (OperationCanceledException)
                {
                    // The request token was cancelled during the store; the request may surface it.
                }

                await WaitUntilAsync(
                    () => Task.FromResult(middleware.PendingMutationCount == 0),
                    TimeSpan.FromSeconds(10),
                    "Background cache mutations did not drain.");

                Assert.AreEqual(3, storage.SetKeys.Count, "The 304 stored a new variant.");
                Assert.IsTrue(requestCts.IsCancellationRequested);
                Assert.IsNull(
                    await storage.GetAsync(evictedKey),
                    "The evicted variant left the index, so it must leave storage too.");
                Assert.LessOrEqual(middleware.GetVariantCount(HttpMethod.GET, VaryBoundUri), 2);
                middleware.Dispose();
            }).GetAwaiter().GetResult();
        }

        // Cancels a token right after the Nth SetAsync, and honours cancellation in RemoveAsync
        // the way a real asynchronous storage would.
        private sealed class CancelOnSetStorage : ICacheStorage
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage(maxEntries: 1000);
            private readonly CancellationTokenSource _cancelOnSet;
            private readonly int _cancelOnSetNumber;
            private readonly object _gate = new object();
            private readonly List<string> _setKeys = new List<string>();

            internal CancelOnSetStorage(CancellationTokenSource cancelOnSet, int cancelOnSetNumber)
            {
                _cancelOnSet = cancelOnSet;
                _cancelOnSetNumber = cancelOnSetNumber;
            }

            internal IReadOnlyList<string> SetKeys
            {
                get
                {
                    lock (_gate)
                        return _setKeys.ToArray();
                }
            }

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
                => _inner.GetAsync(key, cancellationToken);

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                await _inner.SetAsync(key, entry, cancellationToken).ConfigureAwait(false);
                int count;
                lock (_gate)
                {
                    _setKeys.Add(key);
                    count = _setKeys.Count;
                }

                if (count == _cancelOnSetNumber)
                    _cancelOnSet.Cancel();
            }

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return _inner.RemoveAsync(key, cancellationToken);
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
                => _inner.ClearAsync(cancellationToken);

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
                => _inner.GetCountAsync(cancellationToken);

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
                => _inner.GetSizeAsync(cancellationToken);
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
