using System;
using System.Globalization;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// The stored representation: the store owns its header copy (#61), a 304 freshens it per RFC 9111 sections 3.2 and
    /// 4.3.4 with Expires - Date freshness (#68), and trailers are replayed on hits (#155).
    /// </summary>
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_ConsumerMutatesBufferedResponseHeaders_StoredEntryKeepsOriginal()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("ETag", "\"v1\"");
                    headers.Set("X-Origin", "origin");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            200,
                            headers,
                            new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload"), HttpHeaders.Empty),
                            ctx)
                        .AsTask();
                });

                // The consumer (outside the cache) rewrites the headers it was handed before the store has run.
                int mutations = 0;
                var mutator = new HeaderMutatingInterceptor(headers =>
                {
                    if (Interlocked.Increment(ref mutations) != 1)
                        return;

                    headers.Set("Cache-Control", "no-store");
                    headers.Remove("ETag");
                    headers.Set("X-Origin", "consumer");
                    headers.Add("X-Consumer", "added");
                });

                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { mutator, cache }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/consumer-mutation"));

                using (var first = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                    Assert.AreEqual("consumer", first.Headers.Get("X-Origin"));

                await AwaitStoredAsync(storage);
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(1, callCount, "The consumer's no-store must not reach the stored entry.");
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual("max-age=60", hit.Headers.Get("Cache-Control"));
                Assert.AreEqual("\"v1\"", hit.Headers.Get("ETag"));
                Assert.AreEqual("origin", hit.Headers.Get("X-Origin"));
                Assert.IsNull(hit.Headers.Get("X-Consumer"));
            });
        }

        [Test]
        public void CacheInterceptor_ConsumerMutatesHeadersConcurrentlyWithStore_StoreKeepsOriginal()
        {
            AssertAsync.Run(async () =>
            {
                const int Rounds = 20;
                for (int round = 0; round < Rounds; round++)
                {
                    var storage = new SignalingCacheStorage();
                    var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                    var transport = new CallbackTransport((req, handler, ctx, ct) =>
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "max-age=60");
                        headers.Set("X-Origin", "origin");
                        handler.OnRequestStart(req, ctx);
                        return handler.OnResponseStartAsync(
                                200,
                                headers,
                                new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload"), HttpHeaders.Empty),
                                ctx)
                            .AsTask();
                    });

                    // The mutation loop runs on another thread until the background store has handed its entry to
                    // storage, so it overlaps the store's header snapshot.
                    Task mutationLoop = null;
                    var mutator = new HeaderMutatingInterceptor(headers =>
                    {
                        if (mutationLoop != null)
                            return;

                        mutationLoop = Task.Run(() =>
                        {
                            for (int i = 0; i < 1_000_000 && !storage.FirstSet.IsCompleted; i++)
                            {
                                headers.Set("X-Race", i.ToString(CultureInfo.InvariantCulture));
                                headers.Remove("X-Origin");
                                headers.Add("X-Origin", "consumer");
                                headers.Remove("X-Race");
                            }
                        });
                    });

                    var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { mutator, cache }, transport);
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/concurrent-mutation"));

                    using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                    {
                    }

                    await mutationLoop;
                    await AwaitStoredAsync(storage);
                    using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                    Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"), "round " + round);
                    Assert.AreEqual("origin", hit.Headers.Get("X-Origin"), "round " + round);
                    Assert.IsNull(hit.Headers.Get("X-Race"), "round " + round);
                }
            });
        }

        [Test]
        public void CacheInterceptor_Revalidation304_NeverChangesFramingHeadersAndDropsStoredAge()
        {
            AssertAsync.Run(async () =>
            {
                var body = Encoding.UTF8.GetBytes("stored representation");
                var storedLength = body.Length.ToString(CultureInfo.InvariantCulture);
                var cache = new CacheInterceptor(new CachePolicy { Storage = new MemoryCacheStorage() });
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"v1\"");
                    handler.OnRequestStart(req, ctx);
                    if (call == 1)
                    {
                        // An upstream cache already held this response for 3000 s.
                        headers.Set("Cache-Control", "max-age=0, must-revalidate");
                        headers.Set("Content-Type", "text/plain");
                        headers.Set("Content-Length", storedLength);
                        headers.Set("Age", "3000");
                        return handler.OnResponseStartAsync(200, headers, new BufferedResponseBodySource(body), ctx).AsTask();
                    }

                    // A 304 carrying framing metadata and no Age: it was validated by the origin just now.
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Content-Type", "text/plain; charset=utf-8");
                    headers.Set("Content-Length", "999");
                    headers.Set("Content-Range", "bytes 0-998/999");
                    headers.Set("Transfer-Encoding", "chunked");
                    return handler.OnResponseStartAsync(304, headers, new BufferedResponseBodySource(Array.Empty<byte>()), ctx).AsTask();
                });

                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { cache }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/304-framing"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                using (var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(2, callCount);
                    Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                    AssertStoredRepresentation(revalidated, storedLength);
                }

                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount, "The stored Age: 3000 must not survive the 304 and make the entry stale.");
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                AssertStoredRepresentation(hit, storedLength);
                var age = int.Parse(hit.Headers.Get("Age"), CultureInfo.InvariantCulture);
                Assert.Less(age, 60, "Age restarts at the 304's validation.");
            });
        }

        [Test]
        public void CacheInterceptor_ExpiresFreshness_UsesDateNotDeviceClock_WhenDeviceIsBehind()
        {
            AssertAsync.Run(async () =>
            {
                // The device clock is an hour behind the origin; the origin grants 60 s (Expires - Date).
                var deviceStart = DateTime.UtcNow;
                var originDate = deviceStart.AddHours(1);
                var clock = new TestClock(deviceStart);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ExpiresTransport(calls, originDate, originDate.AddSeconds(60)));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expires-behind"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                clock.Set(deviceStart.AddSeconds(30));
                using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(1, calls[0]);
                    Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                }

                clock.Set(deviceStart.AddSeconds(61));
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                    Assert.AreEqual(2, calls[0], "61 s after receipt the 60 s lifetime has run out, whatever the absolute Expires says.");
            });
        }

        [Test]
        public void CacheInterceptor_ExpiresFreshness_UsesDateNotDeviceClock_WhenDeviceIsAhead()
        {
            AssertAsync.Run(async () =>
            {
                // The device clock is an hour ahead of the origin; the origin grants 120 s (Expires - Date).
                var deviceStart = DateTime.UtcNow;
                var originDate = deviceStart.AddHours(-1);
                var clock = new TestClock(deviceStart);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ExpiresTransport(calls, originDate, originDate.AddSeconds(120)));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expires-ahead"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                clock.Set(deviceStart.AddSeconds(60));
                using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(1, calls[0], "An Expires already past on the device clock is still 120 s of lifetime.");
                    Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                    Assert.AreEqual("60", hit.Headers.Get("Age"));
                }

                clock.Set(deviceStart.AddSeconds(121));
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                    Assert.AreEqual(2, calls[0]);
            });
        }

        [Test]
        public void CacheInterceptor_BufferedResponseTrailers_ReplayedOnHit()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Checksum", "abc123");
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Trailer", "X-Checksum");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            200,
                            headers,
                            new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload"), trailers),
                            ctx)
                        .AsTask();
                });

                await AssertTrailersReplayedAsync(transport, "https://example.test/trailers-buffered", () => callCount);
            });
        }

        [Test]
        public void CacheInterceptor_StreamedResponseTrailers_ReplayedOnHitAndAfter304()
        {
            AssertAsync.Run(async () =>
            {
                var trailers = new HttpHeaders();
                trailers.Set("X-Checksum", "abc123");
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    var call = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("ETag", "\"t1\"");
                    handler.OnRequestStart(req, ctx);
                    if (call == 1)
                    {
                        headers.Set("Cache-Control", "max-age=60");
                        return handler.OnResponseStartAsync(
                                200,
                                headers,
                                new MockResponseBodySource(
                                    new[]
                                    {
                                        (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes("pay"),
                                        Encoding.UTF8.GetBytes("load")
                                    },
                                    length: null,
                                    trailers: trailers,
                                    exposeBufferedData: false),
                                ctx)
                            .AsTask();
                    }

                    headers.Set("Cache-Control", "max-age=60");
                    return handler.OnResponseStartAsync(304, headers, new BufferedResponseBodySource(Array.Empty<byte>()), ctx).AsTask();
                });

                var cache = await AssertTrailersReplayedAsync(transport, "https://example.test/trailers-streamed", () => callCount);

                // A forced revalidation answered by 304 keeps the stored trailers (a 304 has no content of its own).
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/trailers-streamed"));
                request.Headers.Set("Cache-Control", "no-cache");
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { cache.Interceptor }, transport);
                using var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                Assert.AreEqual("abc123", revalidated.Trailers.Get("X-Checksum"));
            });
        }

        private static async Task<(CacheInterceptor Interceptor, SignalingCacheStorage Storage)> AssertTrailersReplayedAsync(
            IHttpTransport transport,
            string url,
            Func<int> callCount)
        {
            var storage = new SignalingCacheStorage();
            var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
            var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { cache }, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri(url));

            using (var first = await pipeline.ExecuteAsync(request, new RequestContext(request)))
            {
                Assert.AreEqual("payload", first.GetBodyAsString());
                Assert.AreEqual("abc123", first.Trailers.Get("X-Checksum"));
            }

            await AwaitStoredAsync(storage);
            using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(1, callCount());
            Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
            Assert.AreEqual("payload", hit.GetBodyAsString());
            Assert.AreEqual("abc123", hit.Trailers.Get("X-Checksum"), "A hit replays the stored trailers.");
            Assert.IsNull(hit.Headers.Get("X-Checksum"), "Trailers stay apart from header fields (RFC 9111 section 3.1).");
            return (cache, storage);
        }

        // Bounds the wait so a store that never happens fails the test instead of hanging it.
        private static async Task AwaitStoredAsync(SignalingCacheStorage storage)
        {
            var completed = await Task.WhenAny(storage.FirstSet, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            Assert.AreSame(storage.FirstSet, completed, "The response was never stored.");
        }

        private static void AssertStoredRepresentation(UHttpResponse response, string storedLength)
        {
            Assert.AreEqual("stored representation", response.GetBodyAsString());
            Assert.AreEqual(storedLength, response.Headers.Get("Content-Length"));
            Assert.IsNull(response.Headers.Get("Content-Range"));
            Assert.IsNull(response.Headers.Get("Transfer-Encoding"));
            Assert.AreEqual("text/plain; charset=utf-8", response.Headers.Get("Content-Type"), "Content-Type is freshened (RFC 9111 section 3.2).");
        }

        private static CallbackTransport ExpiresTransport(int[] calls, DateTime originDate, DateTime expires)
        {
            return new CallbackTransport((req, handler, ctx, ct) =>
            {
                Interlocked.Increment(ref calls[0]);
                var headers = new HttpHeaders();
                headers.Set("Date", originDate.ToString("r", CultureInfo.InvariantCulture));
                headers.Set("Expires", expires.ToString("r", CultureInfo.InvariantCulture));
                handler.OnRequestStart(req, ctx);
                return handler.OnResponseStartAsync(
                        200,
                        headers,
                        new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload")),
                        ctx)
                    .AsTask();
            });
        }

        private sealed class TestClock
        {
            private long _ticks;

            internal TestClock(DateTime start)
            {
                Set(start);
            }

            internal void Set(DateTime utc)
            {
                Interlocked.Exchange(ref _ticks, utc.Ticks);
            }

            internal DateTime Read()
            {
                return new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
            }
        }

        // Completes FirstSet when the background store hands its first entry to storage.
        private sealed class SignalingCacheStorage : ICacheStorage
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage();
            private readonly TaskCompletionSource<bool> _firstSet =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal Task FirstSet => _firstSet.Task;

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(key, cancellationToken);
            }

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                await _inner.SetAsync(key, entry, cancellationToken).ConfigureAwait(false);
                _firstSet.TrySetResult(true);
            }

            public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.RemoveAsync(key, cancellationToken);
            }

            public Task ClearAsync(CancellationToken cancellationToken = default)
            {
                return _inner.ClearAsync(cancellationToken);
            }

            public Task<int> GetCountAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetCountAsync(cancellationToken);
            }

            public Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
            {
                return _inner.GetSizeAsync(cancellationToken);
            }
        }

        // Hands the response headers to a callback before passing them on, like a consumer that edits
        // UHttpResponse.Headers.
        private sealed class HeaderMutatingInterceptor : IHttpInterceptor
        {
            private readonly Action<HttpHeaders> _mutate;

            internal HeaderMutatingInterceptor(Action<HttpHeaders> mutate)
            {
                _mutate = mutate;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, ct) => next(request, new MutatingHandler(handler, _mutate), context, ct);
            }

            private sealed class MutatingHandler : IHttpHandler
            {
                private readonly IHttpHandler _inner;
                private readonly Action<HttpHeaders> _mutate;

                internal MutatingHandler(IHttpHandler inner, Action<HttpHeaders> mutate)
                {
                    _inner = inner;
                    _mutate = mutate;
                }

                public void OnRequestStart(UHttpRequest request, RequestContext context)
                {
                    _inner.OnRequestStart(request, context);
                }

                public ValueTask OnResponseStartAsync(
                    int statusCode,
                    HttpHeaders headers,
                    IResponseBodySource body,
                    RequestContext context)
                {
                    _mutate(headers);
                    return _inner.OnResponseStartAsync(statusCode, headers, body, context);
                }

                public void OnResponseError(UHttpException error, RequestContext context)
                {
                    _inner.OnResponseError(error, context);
                }
            }
        }
    }
}
