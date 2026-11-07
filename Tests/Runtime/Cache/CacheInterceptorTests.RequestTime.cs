using System;
using System.Globalization;
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
    /// RFC 9111 section 4.2.3 (#306): the Age of a stored response is corrected by response_delay = response_time -
    /// request_time, measured on the interceptor clock from sending the request to receiving the response headers.
    /// </summary>
    public partial class CacheInterceptorTests
    {
        [Test]
        public void CacheInterceptor_RequestTime_BufferedResponse_AddsResponseDelayToAge()
        {
            AssertAsync.Run(async () =>
            {
                // Age: 10, max-age=60, received 5 s after the request was sent: fresh for 45 s, Age counted from 15.
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var calls = new int[1];
                var transport = DelayedResponseTransport(
                    calls, clock, TimeSpan.FromSeconds(5), age: "10", streaming: false);

                await AssertFreshFor45SecondsWithAgeFrom15Async(clock, start, calls, transport, "/request-time-buffered");
            });
        }

        [Test]
        public void CacheInterceptor_RequestTime_StreamingResponse_AddsResponseDelayToAge()
        {
            AssertAsync.Run(async () =>
            {
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var calls = new int[1];
                var transport = DelayedResponseTransport(
                    calls, clock, TimeSpan.FromSeconds(5), age: "10", streaming: true);

                await AssertFreshFor45SecondsWithAgeFrom15Async(clock, start, calls, transport, "/request-time-streaming");
            });
        }

        [Test]
        public void CacheInterceptor_RequestTime_ClockSteppingBack_KeepsUpstreamAge()
        {
            AssertAsync.Run(async () =>
            {
                // response_time before request_time (the device clock was set back): response_delay is 0, never negative.
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    DelayedResponseTransport(calls, clock, TimeSpan.FromSeconds(-30), age: "10", streaming: false));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/request-time-backwards"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(1, calls[0]);
                    Assert.AreEqual("10", hit.Headers.Get("Age"));
                }
            });
        }

        [Test]
        public void CacheInterceptor_RequestTime_StoreWithoutRequestTime_KeepsUpstreamAge()
        {
            AssertAsync.Run(async () =>
            {
                // A store without request_time (as entries stored before #306 were) keeps response_delay 0: it is
                // served with Age = upstream Age + resident time, nothing invented for it.
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var uri = new Uri("https://example.test/request-time-none");
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                headers.Set("Age", "10");
                var body = new SegmentedBuffer();
                body.Write(Encoding.UTF8.GetBytes("payload"));
                cache.QueueStoreResponse(
                    HttpMethod.GET,
                    uri,
                    new HttpHeaders(),
                    "GET " + CacheInterceptor.NormalizeUri(uri),
                    200,
                    headers,
                    body,
                    trailers: null,
                    responseTimeUtc: start);
                await AwaitStoredAsync(storage);

                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    DelayedResponseTransport(calls, clock, TimeSpan.FromSeconds(5), age: "10", streaming: false));
                var request = new UHttpRequest(HttpMethod.GET, uri);
                await AssertHitThenStaleAsync(pipeline, request, clock, calls, start.AddSeconds(20), freshSeconds: 30, ageAtReceipt: 30, callsSoFar: 0);
            });
        }

        [Test]
        public void CacheInterceptor_RequestTime_NotModified_CorrectsAgeByRevalidationDelay()
        {
            AssertAsync.Run(async () =>
            {
                // The 304 carries Age: 10 and arrives 3 s after the conditional request: the freshened entry starts at
                // Age 13 and stays fresh for 47 s.
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    RevalidationDelayTransport(calls, clock, TimeSpan.FromSeconds(3), modifiedStatus: 304));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/request-time-304"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                var revalidationSent = start.AddSeconds(61);
                clock.Set(revalidationSent);
                using (var revalidated = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                    Assert.AreEqual(2, calls[0]);
                    Assert.AreEqual("REVALIDATED", revalidated.Headers.Get("X-Cache"));
                    Assert.AreEqual("13", revalidated.Headers.Get("Age"));
                }

                await AwaitSignalAsync(storage.SecondSet, "The freshened entry was never stored.");
                var received = revalidationSent.AddSeconds(3);
                await AssertHitThenStaleAsync(pipeline, request, clock, calls, received, freshSeconds: 47, ageAtReceipt: 13, callsSoFar: 2);
            });
        }

        [Test]
        public void CacheInterceptor_RequestTime_ModifiedAfterRevalidation_UsesConditionalRequestTime()
        {
            AssertAsync.Run(async () =>
            {
                // A 200 replacing the stale entry: its storing handler is created after the response arrived, so the
                // delay must come from the conditional request's send time.
                var start = WholeSecondsUtcNow();
                var clock = new TestClock(start);
                var storage = new SignalingCacheStorage();
                var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
                cache.UtcNowProvider = clock.Read;
                var calls = new int[1];
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache },
                    RevalidationDelayTransport(calls, clock, TimeSpan.FromSeconds(5), modifiedStatus: 200));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/request-time-200"));

                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                {
                }

                await AwaitStoredAsync(storage);
                var revalidationSent = start.AddSeconds(61);
                clock.Set(revalidationSent);
                using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                    Assert.AreEqual(2, calls[0]);

                await AwaitSignalAsync(storage.SecondSet, "The replacement was never stored.");
                var received = revalidationSent.AddSeconds(5);
                await AssertHitThenStaleAsync(pipeline, request, clock, calls, received, freshSeconds: 45, ageAtReceipt: 15, callsSoFar: 2);
            });
        }

        private static async Task AssertFreshFor45SecondsWithAgeFrom15Async(
            TestClock clock,
            DateTime start,
            int[] calls,
            CallbackTransport transport,
            string path)
        {
            var storage = new SignalingCacheStorage();
            var cache = new CacheInterceptor(new CachePolicy { Storage = storage });
            cache.UtcNowProvider = clock.Read;
            var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { cache }, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test" + path));

            using (var first = await pipeline.ExecuteAsync(request, new RequestContext(request)))
                Assert.AreEqual("payload", first.GetBodyAsString());

            await AwaitStoredAsync(storage);
            await AssertHitThenStaleAsync(pipeline, request, clock, calls, start.AddSeconds(5), freshSeconds: 45, ageAtReceipt: 15, callsSoFar: 1);
        }

        private static async Task AssertHitThenStaleAsync(
            TestInterceptorPipeline pipeline,
            UHttpRequest request,
            TestClock clock,
            int[] calls,
            DateTime received,
            int freshSeconds,
            int ageAtReceipt,
            int callsSoFar)
        {
            clock.Set(received);
            using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
            {
                Assert.AreEqual(callsSoFar, calls[0]);
                Assert.AreEqual("HIT", hit.Headers.Get("X-Cache"));
                Assert.AreEqual(ageAtReceipt.ToString(CultureInfo.InvariantCulture), hit.Headers.Get("Age"), "Age counts from the corrected initial age.");
            }

            clock.Set(received.AddSeconds(freshSeconds - 1));
            using (var hit = await pipeline.ExecuteAsync(request, new RequestContext(request)))
            {
                Assert.AreEqual(callsSoFar, calls[0], "Still fresh one second before the corrected lifetime runs out.");
                Assert.AreEqual(
                    (ageAtReceipt + freshSeconds - 1).ToString(CultureInfo.InvariantCulture),
                    hit.Headers.Get("Age"));
            }

            clock.Set(received.AddSeconds(freshSeconds));
            using (await pipeline.ExecuteAsync(request, new RequestContext(request)))
                Assert.AreEqual(callsSoFar + 1, calls[0], "Stale once current_age reaches max-age.");
        }

        // Every call: a max-age=60 response carrying `age` whose headers arrive `delay` after the request was sent
        // (the clock moves between OnRequestStart and OnResponseStartAsync).
        private static CallbackTransport DelayedResponseTransport(
            int[] calls,
            TestClock clock,
            TimeSpan delay,
            string age,
            bool streaming)
        {
            return new CallbackTransport((req, handler, ctx, ct) =>
            {
                Interlocked.Increment(ref calls[0]);
                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                headers.Set("Age", age);
                handler.OnRequestStart(req, ctx);
                clock.Set(clock.Read().Add(delay));
                var payload = Encoding.UTF8.GetBytes("payload");
                IResponseBodySource body = streaming
                    ? new MockResponseBodySource(
                        new[] { (ReadOnlyMemory<byte>)payload },
                        length: payload.Length,
                        trailers: HttpHeaders.Empty,
                        exposeBufferedData: false)
                    : new BufferedResponseBodySource(payload);
                return handler.OnResponseStartAsync(200, headers, body, ctx).AsTask();
            });
        }

        // Call 1: an immediate 200 with an ETag and max-age=60, no Age. Later calls: `modifiedStatus` (304, or 200 with
        // a new body) carrying Age: 10, arriving `delay` after the conditional request was sent.
        private static CallbackTransport RevalidationDelayTransport(
            int[] calls,
            TestClock clock,
            TimeSpan delay,
            int modifiedStatus)
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
                    return handler.OnResponseStartAsync(
                            200,
                            headers,
                            new BufferedResponseBodySource(Encoding.UTF8.GetBytes("payload")),
                            ctx)
                        .AsTask();
                }

                headers.Set("Age", "10");
                clock.Set(clock.Read().Add(delay));
                var body = modifiedStatus == 304
                    ? Array.Empty<byte>()
                    : Encoding.UTF8.GetBytes("payload v2");
                return handler.OnResponseStartAsync(modifiedStatus, headers, new BufferedResponseBodySource(body), ctx).AsTask();
            });
        }
    }
}
