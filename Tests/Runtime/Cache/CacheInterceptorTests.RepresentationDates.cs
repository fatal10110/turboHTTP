using System;
using System.Globalization;
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
    /// Stored Date and Expires handling (#68): the receive time stands in for a missing Date (RFC 9110 section 6.6.1),
    /// extreme Expires - Date lifetimes are clamped, an invalid Expires is already expired (RFC 9111 section 5.3), and a
    /// 304's Set-Cookie is not merged into storage when the policy refuses Set-Cookie responses.
    /// </summary>
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_ResponseWithoutDate_StoresReceiveTimeAsDate()
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
                    RevalidatingTransport(calls, originDate: null, notModifiedSetCookie: null));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/no-date"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                clock.Set(receivedAt.AddSeconds(5));
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(1, calls[0]);
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual(
                    receivedAt.ToString("r", CultureInfo.InvariantCulture),
                    hit.Headers.Get("Date"),
                    "A cache appends the receive time as Date when the origin sent none (RFC 9110 section 6.6.1).");
            });
        }

        [Test]
        public void CacheInterceptor_NotModifiedWithoutDate_ReplacesStoredDateWithReceiveTime()
        {
            AssertAsync.Run(async () =>
            {
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    RevalidatingTransport(calls, originDate: start.AddMinutes(-10), notModifiedSetCookie: null));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/304-no-date"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                var revalidatedAt = start.AddSeconds(20);
                clock.Set(revalidatedAt);
                var expectedDate = revalidatedAt.ToString("r", CultureInfo.InvariantCulture);
                var forced = new UHttpRequest(HttpMethod.GET, request.Uri);
                forced.Headers.Set("Cache-Control", "no-cache");
                using (var revalidated = await pipeline.ExecuteAsync(forced, new RequestContext(forced)))
                {
                    Assert.AreEqual(2, calls[0]);
                    Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                    Assert.AreEqual(expectedDate, revalidated.Headers.Get("Date"), "The 304 is the new response; its receive time is the Date.");
                }

                await AwaitSignalAsync(storage.SecondSet, "The freshened entry was never stored.");
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, calls[0]);
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual(expectedDate, hit.Headers.Get("Date"));
            });
        }

        [Test]
        public void CacheInterceptor_ExpiresFarBeyondDate_ClampsLifetimeAndServesHit()
        {
            AssertAsync.Run(async () =>
            {
                // Expires - Date is about 8000 years: added to the receive time it overflows DateTime.
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    ExpiresTransport(
                        calls,
                        new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        new DateTime(9999, 12, 31, 23, 59, 59, DateTimeKind.Utc)));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expires-9999"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(1, calls[0]);
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_InvalidExpires_IsAlreadyExpired_EvenWithHeuristicFreshness()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy
                {
                    Storage = storage,
                    EnableHeuristicFreshness = true,
                    DoNotCacheWithoutFreshness = false
                });
                int calls = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    var headers = new HttpHeaders();
                    headers.Set("Expires", "not-a-date");
                    headers.Set("ETag", "\"e1\"");
                    handler.OnRequestStart(req, ctx);
                    return handler.OnResponseStartAsync(
                            200,
                            headers,
                            new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload")),
                            ctx)
                        .AsTask();
                });
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { cache }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/invalid-expires"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using var second = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, calls, "An invalid Expires MUST be treated as already expired (RFC 9111 section 5.3).");
                Assert.AreNotEqual("HIT", second.Headers.Get("X-Cache"));
            });
        }

        [Test]
        public void CacheInterceptor_NotModifiedSetCookie_ReachesRevalidatingConsumerButIsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    RevalidatingTransport(calls, originDate: null, notModifiedSetCookie: "sid=304"));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/304-set-cookie"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                var forced = new UHttpRequest(HttpMethod.GET, request.Uri);
                forced.Headers.Set("Cache-Control", "no-cache");
                using (var revalidated = await pipeline.ExecuteAsync(forced, new RequestContext(forced)))
                {
                    Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                    Assert.AreEqual("sid=304", revalidated.Headers.Get("Set-Cookie"), "The 304's cookie is part of this response.");
                }

                await AwaitSignalAsync(storage.SecondSet, "The freshened entry was never stored.");
                using var hit = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(2, calls[0]);
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.IsNull(
                    hit.Headers.Get("Set-Cookie"),
                    "AllowSetCookieResponses = false keeps a 304's Set-Cookie out of storage, so later hits never replay it.");
            });
        }

        private static DateTime WholeSecondsUtcNow()
        {
            var ticks = DateTime.UtcNow.Ticks;
            return new DateTime(ticks - (ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }

        // Call 1: 200 with an ETag and max-age=60 (Date only when originDate is set). Later calls: a 304 without Date,
        // optionally carrying Set-Cookie.
        private static CallbackTransport RevalidatingTransport(int[] calls, DateTime? originDate, string notModifiedSetCookie)
        {
            return new CallbackTransport((req, handler, ctx, ct) =>
            {
                var call = Interlocked.Increment(ref calls[0]);
                var headers = new HttpHeaders();
                headers.Set("ETag", "\"v1\"");
                headers.Set("Cache-Control", "max-age=60");
                handler.OnRequestStart(req, ctx);
                if (call == 1)
                {
                    if (originDate.HasValue)
                        headers.Set("Date", originDate.Value.ToString("r", CultureInfo.InvariantCulture));
                    return handler.OnResponseStartAsync(
                            200,
                            headers,
                            new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload")),
                            ctx)
                        .AsTask();
                }

                if (notModifiedSetCookie != null)
                    headers.Set("Set-Cookie", notModifiedSetCookie);
                return handler.OnResponseStartAsync(304, headers, new BufferedResponseBodySource(Array.Empty<byte>()), ctx).AsTask();
            });
        }
    }
}
