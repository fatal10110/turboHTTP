using System;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// Oversize delta-seconds are clamped to 2^31 s instead of throwing or being dropped (RFC 9111 section 1.2.2; #457,
    /// #459), and a response without explicit or heuristic freshness is stale, so it is revalidated before every reuse
    /// (RFC 9111 section 4.2; #483).
    /// </summary>
    public partial class CacheInterceptorTests
    {
        private static readonly TimeSpan TwoToThe31Seconds = TimeSpan.FromSeconds(2147483648d);

        [TestCase("999999999999")]
        [TestCase("99999999999999999999")]
        public void CacheInterceptor_OversizeMaxAge_IsCachedWithClampedLifetime(string maxAge)
        {
            AssertAsync.Run(async () =>
            {
                var receivedAt = WholeSecondsUtcNow();
                var clock = new TestClock(receivedAt);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ScriptedTransport(calls, (call, req) => (200, Headers("Cache-Control", "max-age=" + maxAge))));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/max-age-" + maxAge.Length));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                clock.Set(receivedAt + TwoToThe31Seconds - TimeSpan.FromSeconds(1));
                using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                    Assert.AreEqual(1, calls[0]);
                }

                clock.Set(receivedAt + TwoToThe31Seconds);
                using (var expired = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreNotEqual("HIT", expired.Headers.Get("X-Cache"), "The lifetime is 2^31 s (RFC 9111 section 1.2.2).");
                    Assert.AreEqual(2, calls[0]);
                }
            });
        }

        [Test]
        public void CacheInterceptor_NotModifiedWithOversizeMaxAge_ServesStoredEntry()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ScriptedTransport(calls, (call, req) => call == 1
                        ? (200, Headers("Cache-Control", "max-age=0"))
                        : (304, Headers("Cache-Control", "max-age=999999999999"))));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/304-huge-max-age"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using (var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                    Assert.AreEqual("payload", revalidated.GetBodyAsString());
                }

                await AwaitSignalAsync(storage.SecondSet, "The 304 merge was never stored.");
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual(2, calls[0]);
            });
        }

        [Test]
        public void CacheInterceptor_StaleEntryWithOversizeStaleWhileRevalidate_IsServedStale()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ScriptedTransport(calls, (call, req) => call == 1
                        ? (200, Headers("Cache-Control", "max-age=0, stale-while-revalidate=300000000000"))
                        : (304, Headers("Cache-Control", "max-age=0, stale-while-revalidate=300000000000"))));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/huge-swr"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using var stale = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual("STALE", stale.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", stale.GetBodyAsString());
            });
        }

        [Test]
        public void CacheInterceptor_ValidatorOnlyResponse_WithoutFreshness_IsRevalidatedOnEveryUse()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy
                {
                    Storage = storage,
                    DoNotCacheWithoutFreshness = false,
                    EnableHeuristicFreshness = false
                });
                var calls = new int[1];
                var conditional = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ScriptedTransport(calls, (call, req) =>
                    {
                        if (call == 1)
                            return (200, new HttpHeaders());

                        Assert.AreEqual("\"v1\"", req.Headers.Get("If-None-Match"));
                        Interlocked.Increment(ref conditional[0]);
                        return (304, new HttpHeaders());
                    }));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/etag-only"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using (var second = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"), "No freshness means stale (RFC 9111 section 4.2).");
                    Assert.AreEqual("payload", second.GetBodyAsString());
                }

                // The 304 carries no freshness either, so the merged entry is stale too (304 merge path).
                await AwaitSignalAsync(storage.SecondSet, "The 304 merge was never stored.");
                using (var third = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("REVALIDATED", third.Headers.Get("X-Cache"));
                }

                Assert.AreEqual(3, calls[0]);
                Assert.AreEqual(2, conditional[0]);
            });
        }

        [Test]
        public void CacheInterceptor_NotModifiedRemovingNoCache_WithoutFreshness_StaysStale()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy
                {
                    Storage = storage,
                    DoNotCacheWithoutFreshness = false,
                    EnableHeuristicFreshness = false
                });
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ScriptedTransport(calls, (call, req) => call == 1
                        ? (200, Headers("Cache-Control", "no-cache"))
                        : (304, Headers("Cache-Control", "public"))));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-cache-then-public"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using (var second = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                }

                // The merged entry lost no-cache and has no freshness: it must not become a HIT forever.
                await AwaitSignalAsync(storage.SecondSet, "The 304 merge was never stored.");
                using (var third = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual("REVALIDATED", third.Headers.Get("X-Cache"));
                }

                Assert.AreEqual(3, calls[0]);
            });
        }

        private static HttpHeaders Headers(string name, string value)
        {
            var headers = new HttpHeaders();
            headers.Set(name, value);
            return headers;
        }

        // Every response carries ETag "v1" and, for a 200, the body "payload". respond(call, request) picks the status
        // and the other headers; call is 1-based.
        private static CallbackTransport ScriptedTransport(
            int[] calls,
            Func<int, UHttpRequest, (int Status, HttpHeaders Headers)> respond)
        {
            return new CallbackTransport((req, handler, ctx, ct) =>
            {
                var call = Interlocked.Increment(ref calls[0]);
                var (status, headers) = respond(call, req);
                headers.Set("ETag", "\"v1\"");
                handler.OnRequestStart(req, ctx);
                var body = status == 304 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes("payload");
                return handler.OnResponseStartAsync(status, headers, new BufferedResponseBodySource(body), ctx).AsTask();
            });
        }
    }
}
