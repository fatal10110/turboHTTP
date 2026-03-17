using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
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
        public void CacheInterceptor_NormalizesQueryParameterOrder()
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

                var requestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data?b=2&a=1"));
                var requestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/data?a=1&b=2"));

                await pipeline.ExecuteAsync(requestA, new RequestContext(requestA));
                var cached = await pipeline.ExecuteAsync(requestB, new RequestContext(requestB));

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", cached.Headers.Get("X-Cache"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_SeparatesAcceptEncodingVariants()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Vary", "Accept-Encoding");

                    var acceptEncoding = req.Headers.Get("Accept-Encoding");
                    var body = string.Equals(acceptEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
                        ? Encoding.UTF8.GetBytes("gzip-body")
                        : Encoding.UTF8.GetBytes("identity-body");

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        body,
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var gzipHeaders = new HttpHeaders();
                gzipHeaders.Set("Accept-Encoding", "gzip");

                var gzipRequestA = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"), gzipHeaders);
                var identityRequest = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"));
                var gzipRequestB = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/asset"), gzipHeaders);

                await pipeline.ExecuteAsync(gzipRequestA, new RequestContext(gzipRequestA));
                await pipeline.ExecuteAsync(identityRequest, new RequestContext(identityRequest));
                var gzipCached = await pipeline.ExecuteAsync(gzipRequestB, new RequestContext(gzipRequestB));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual("HIT", gzipCached.Headers.Get("X-Cache"));
                Assert.AreEqual("gzip-body", gzipCached.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_StripsHopByHopHeadersBeforeStorage()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Connection", "keep-alive, X-Hop");
                    headers.Set("Keep-Alive", "timeout=30");
                    headers.Set("Transfer-Encoding", "chunked");
                    headers.Set("X-Hop", "volatile");
                    headers.Set("X-Stable", "persisted");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("payload"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/hop-strip"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
                Assert.IsFalse(second.Headers.Contains("Connection"));
                Assert.IsFalse(second.Headers.Contains("Keep-Alive"));
                Assert.IsFalse(second.Headers.Contains("Transfer-Encoding"));
                Assert.IsFalse(second.Headers.Contains("X-Hop"));
                Assert.AreEqual("persisted", second.Headers.Get("X-Stable"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_DoesNotStore_WhenVaryHeaderCountExceedsLimit()
        {
            Task.Run(async () =>
            {
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var varyBuilder = new StringBuilder();
                for (int i = 0; i < 40; i++)
                {
                    if (i > 0)
                        varyBuilder.Append(", ");
                    varyBuilder.Append("x-vary-");
                    varyBuilder.Append(i);
                }

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=60");
                    headers.Set("Vary", varyBuilder.ToString());
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        headers,
                        Encoding.UTF8.GetBytes("payload"),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/vary-limit"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, transport.RequestCount);
                Assert.IsNull(second.Headers.Get("X-Cache"));
                Assert.AreEqual(0, await storage.GetCountAsync());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_InvalidatesOnUnsafeMethodForSameUri()
        {
            Task.Run(async () =>
            {
                int getVersion = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Method == HttpMethod.GET)
                    {
                        getVersion++;
                        var getHeaders = new HttpHeaders();
                        getHeaders.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            getHeaders,
                            Encoding.UTF8.GetBytes("v" + getVersion),
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var uri = new Uri("https://example.test/profile");
                var getRequest = new UHttpRequest(HttpMethod.GET, uri);
                var postRequest = new UHttpRequest(HttpMethod.POST, uri, body: Encoding.UTF8.GetBytes("update"));

                var first = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));
                var second = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));
                await pipeline.ExecuteAsync(postRequest, new RequestContext(postRequest));
                var third = await pipeline.ExecuteAsync(getRequest, new RequestContext(getRequest));

                Assert.AreEqual("v1", first.GetBodyAsString());
                Assert.AreEqual("v1", second.GetBodyAsString());
                Assert.AreEqual("v2", third.GetBodyAsString());
                Assert.AreEqual(3, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_UnsafeInvalidation_AlsoInvalidatesLocationAndContentLocation()
        {
            Task.Run(async () =>
            {
                int profileVersion = 0;
                int summaryVersion = 0;

                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Method == HttpMethod.GET && req.Uri.AbsolutePath == "/profile")
                    {
                        profileVersion++;
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("profile-v" + profileVersion),
                            ctx.Elapsed,
                            req));
                    }

                    if (req.Method == HttpMethod.GET && req.Uri.AbsolutePath == "/summary")
                    {
                        summaryVersion++;
                        var headers = new HttpHeaders();
                        headers.Set("Cache-Control", "max-age=60");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("summary-v" + summaryVersion),
                            ctx.Elapsed,
                            req));
                    }

                    var postHeaders = new HttpHeaders();
                    postHeaders.Set("Location", "/profile");
                    postHeaders.Set("Content-Location", "/summary");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        postHeaders,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var profileUri = new Uri("https://example.test/profile");
                var summaryUri = new Uri("https://example.test/summary");
                var updateUri = new Uri("https://example.test/update");

                var profileSeed = new UHttpRequest(HttpMethod.GET, profileUri);
                await pipeline.ExecuteAsync(profileSeed, new RequestContext(profileSeed));
                var summarySeed = new UHttpRequest(HttpMethod.GET, summaryUri);
                await pipeline.ExecuteAsync(summarySeed, new RequestContext(summarySeed));

                var profileCached = new UHttpRequest(HttpMethod.GET, profileUri);
                await pipeline.ExecuteAsync(profileCached, new RequestContext(profileCached));
                var summaryCached = new UHttpRequest(HttpMethod.GET, summaryUri);
                await pipeline.ExecuteAsync(summaryCached, new RequestContext(summaryCached));

                var updateRequest = new UHttpRequest(HttpMethod.POST, updateUri, body: Encoding.UTF8.GetBytes("update"));
                await pipeline.ExecuteAsync(updateRequest, new RequestContext(updateRequest));

                var profileAfterRequest = new UHttpRequest(HttpMethod.GET, profileUri);
                var profileAfter = await pipeline.ExecuteAsync(profileAfterRequest, new RequestContext(profileAfterRequest));
                var summaryAfterRequest = new UHttpRequest(HttpMethod.GET, summaryUri);
                var summaryAfter = await pipeline.ExecuteAsync(summaryAfterRequest, new RequestContext(summaryAfterRequest));

                Assert.AreEqual("profile-v2", profileAfter.GetBodyAsString());
                Assert.AreEqual("summary-v2", summaryAfter.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_RevalidatesExpiredEntries_ThatHaveValidators()
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
                    headers.Set("Cache-Control", "max-age=0");
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
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expired-revalidate"));

                await pipeline.ExecuteAsync(request, new RequestContext(request));
                var second = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(2, callCount);
                Assert.AreEqual("REVALIDATED", second.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", second.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_StaleWhileRevalidate_ServesStaleAndUsesBackgroundContext()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var backgroundContextTcs = new TaskCompletionSource<RequestContext>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var currentCall = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=0, stale-while-revalidate=60");
                    headers.Set("ETag", "\"swr-v1\"");

                    if (currentCall == 1)
                    {
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req));
                    }

                    backgroundContextTcs.TrySetResult(ctx);
                    Assert.AreEqual("\"swr-v1\"", req.Headers.Get("If-None-Match"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.NotModified,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/stale-while-revalidate");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));
                await WaitUntilAsync(
                    async () => await storage.GetCountAsync().ConfigureAwait(false) == 1,
                    TimeSpan.FromSeconds(1),
                    "Initial cache store did not complete.");

                var secondRequest = new UHttpRequest(HttpMethod.GET, uri);
                var secondContext = new RequestContext(secondRequest);
                var second = await pipeline.ExecuteAsync(secondRequest, secondContext);

                Assert.AreEqual("STALE", second.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", second.GetBodyAsString());
                Assert.IsFalse(secondContext.Timeline.Any(evt => evt.Name == "CacheRevalidate"));

                var backgroundContext = await TestHelpers.AssertCompletesWithinAsync(
                    backgroundContextTcs.Task,
                    TimeSpan.FromSeconds(1),
                    "Background revalidation did not start.");

                Assert.AreNotSame(secondContext, backgroundContext);
                Assert.AreNotSame(secondContext.Request, backgroundContext.Request);
                Assert.AreEqual(2, callCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_QueuesStoreAfterInnerResponseEndReturns()
        {
            Task.Run(async () =>
            {
                var storage = new BlockingCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new InterceptorPipeline(new[] { middleware }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/store-order"));
                var context = new RequestContext(request);
                using var handler = new BlockingEndHandler();

                var dispatchTask = Task.Run(async () =>
                {
                    await pipeline.Pipeline(request, handler, context, CancellationToken.None).ConfigureAwait(false);
                });

                Assert.IsTrue(handler.EndEntered.Wait(TimeSpan.FromSeconds(1)));
                Assert.IsFalse(storage.SetStarted.IsSet);

                handler.ReleaseEnd();

                Assert.IsTrue(storage.SetStarted.Wait(TimeSpan.FromSeconds(1)));
                await TestHelpers.AssertCompletesWithinAsync(
                    dispatchTask,
                    TimeSpan.FromSeconds(1),
                    "Dispatch should not wait for cache storage completion.");

                storage.ReleaseSet();
                await TestHelpers.AssertCompletesWithinAsync(
                    storage.SetCompleted.Task,
                    TimeSpan.FromSeconds(1),
                    "Background cache store did not finish after release.");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_WaitsForPendingStoreBeforeServingNextLookup()
        {
            Task.Run(async () =>
            {
                using var storage = new DelayedSetCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/pending-store");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                using var first = await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));

                Assert.IsTrue(storage.SetStarted.Wait(TimeSpan.FromSeconds(1)));

                var secondRequest = new UHttpRequest(HttpMethod.GET, uri);
                var secondTask = pipeline.ExecuteAsync(secondRequest, new RequestContext(secondRequest));

                var earlyCompletion = await Task.WhenAny(secondTask, Task.Delay(50)).ConfigureAwait(false);
                Assert.AreNotSame(secondTask, earlyCompletion, "Second lookup should wait for the pending store.");

                storage.ReleaseSet();

                using var second = await TestHelpers.AssertCompletesWithinAsync(
                    secondTask,
                    TimeSpan.FromSeconds(1),
                    "Pending store did not unblock the next cache lookup.");

                Assert.AreEqual(1, transport.RequestCount);
                Assert.AreEqual("HIT", second.Headers.Get("X-Cache"));
                Assert.AreEqual("payload", second.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_FaultedPendingStore_DoesNotAbortNextLookup()
        {
            Task.Run(async () =>
            {
                using var storage = new FailingDelayedSetCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "max-age=60");
                var transport = new MockTransport(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes("payload"));

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/faulted-pending-store");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                using var first = await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));

                Assert.IsTrue(storage.SetStarted.Wait(TimeSpan.FromSeconds(1)));

                var secondRequest = new UHttpRequest(HttpMethod.GET, uri);
                var secondTask = pipeline.ExecuteAsync(secondRequest, new RequestContext(secondRequest));

                var earlyCompletion = await Task.WhenAny(secondTask, Task.Delay(50)).ConfigureAwait(false);
                Assert.AreNotSame(secondTask, earlyCompletion, "Second lookup should wait for the pending store.");

                storage.FailSet();

                using var second = await TestHelpers.AssertCompletesWithinAsync(
                    secondTask,
                    TimeSpan.FromSeconds(1),
                    "Faulted pending store should not abort the next cache lookup.");

                Assert.AreEqual(2, transport.RequestCount);
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
                Assert.AreEqual("payload", second.GetBodyAsString());
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CacheInterceptor_Dispose_CancelsBackgroundRevalidation()
        {
            Task.Run(async () =>
            {
                int callCount = 0;
                var storage = new MemoryCacheStorage();
                var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });
                var backgroundStarted = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var backgroundCanceled = new TaskCompletionSource<object>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    var currentCall = Interlocked.Increment(ref callCount);
                    var headers = new HttpHeaders();
                    headers.Set("Cache-Control", "max-age=0, stale-while-revalidate=60");
                    headers.Set("ETag", "\"dispose-v1\"");

                    if (currentCall == 1)
                    {
                        return new UHttpResponse(
                            HttpStatusCode.OK,
                            headers,
                            Encoding.UTF8.GetBytes("payload"),
                            ctx.Elapsed,
                            req);
                    }

                    backgroundStarted.TrySetResult(null);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        backgroundCanceled.TrySetResult(null);
                        throw;
                    }

                    throw new AssertionException("Background revalidation should have been canceled.");
                });

                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
                var uri = new Uri("https://example.test/stale-dispose");

                var firstRequest = new UHttpRequest(HttpMethod.GET, uri);
                await pipeline.ExecuteAsync(firstRequest, new RequestContext(firstRequest));
                await WaitUntilAsync(
                    async () => await storage.GetCountAsync().ConfigureAwait(false) == 1,
                    TimeSpan.FromSeconds(1),
                    "Initial cache store did not complete.");

                var secondRequest = new UHttpRequest(HttpMethod.GET, uri);
                var stale = await pipeline.ExecuteAsync(secondRequest, new RequestContext(secondRequest));
                Assert.AreEqual("STALE", stale.Headers.Get("X-Cache"));

                await TestHelpers.AssertCompletesWithinAsync(
                    backgroundStarted.Task,
                    TimeSpan.FromSeconds(1),
                    "Background revalidation did not start.");

                middleware.Dispose();

                await TestHelpers.AssertCompletesWithinAsync(
                    backgroundCanceled.Task,
                    TimeSpan.FromSeconds(1),
                    "Background revalidation was not canceled on dispose.");
            }).GetAwaiter().GetResult();
        }
    }
}
