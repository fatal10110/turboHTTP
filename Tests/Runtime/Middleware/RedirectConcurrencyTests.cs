using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.RateLimit;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    /// <summary>
    /// Regression tests for #797: RedirectInterceptor dispatches each hop only after the previous
    /// hop's dispatch returned, so interceptors inside it (ConcurrencyInterceptor) release their
    /// per-hop permit before the next hop acquires one. MockTransport drives the handler callback
    /// inside its dispatch task, as the socket transports do.
    /// </summary>
    [TestFixture]
    public class RedirectConcurrencyTests
    {
        // Failure bound only (the old code deadlocks); never used for synchronization.
        private static readonly TimeSpan DeadlockBound = TimeSpan.FromSeconds(30);

        [Test]
        public void SameHostRedirect_PreviousHopReleasesPermitBeforeNextHopIsDispatched()
        {
            AssertAsync.Run(async () =>
            {
                var releasedBeforeHop = new List<int>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    releasedBeforeHop.Add(CountEvents(ctx, "ConcurrencyReleased"));
                    return Task.FromResult(Respond(req, ctx));
                });

                using var concurrency = new ConcurrencyInterceptor(maxConnectionsPerHost: 2, maxTotalConnections: 2);
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), concurrency }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, releasedBeforeHop,
                    "each hop must release its concurrency permit before the next hop is dispatched");
            });
        }

        [Test]
        public void SameHostRedirectChain_WithSinglePermit_CompletesWithoutCallerCancellation()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) => Task.FromResult(Respond(req, ctx)));

                using var concurrency = new ConcurrencyInterceptor(maxConnectionsPerHost: 1, maxTotalConnections: 1);
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), concurrency }, transport);

                using var deadline = new CancellationTokenSource(DeadlockBound);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request), deadline.Token);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsFalse(deadline.IsCancellationRequested);
            });
        }

        [Test]
        public void SixConcurrentRequestsEachRedirectedOnce_UnderDefaultLimits_AllComplete()
        {
            AssertAsync.Run(async () =>
            {
                const int RequestCount = 6; // the default per-host limit
                int firstHops = 0;
                var allFirstHopsStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath.StartsWith("/start", StringComparison.Ordinal))
                    {
                        // Every request holds its first-hop permit until all six are in flight.
                        if (Interlocked.Increment(ref firstHops) == RequestCount)
                            allFirstHopsStarted.TrySetResult(true);
                        await allFirstHopsStarted.Task.ConfigureAwait(false);

                        var headers = new HttpHeaders();
                        headers.Set("Location", "/final" + req.Uri.AbsolutePath.Substring("/start".Length));
                        return new UHttpResponse(HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req);
                    }

                    return new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req);
                });

                using var concurrency = new ConcurrencyInterceptor();
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), concurrency }, transport);

                using var deadline = new CancellationTokenSource(DeadlockBound);
                var tasks = new Task<UHttpResponse>[RequestCount];
                for (int i = 0; i < RequestCount; i++)
                {
                    var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start" + i));
                    tasks[i] = pipeline.ExecuteAsync(request, new RequestContext(request), deadline.Token);
                }

                var responses = await Task.WhenAll(tasks);
                Assert.IsFalse(deadline.IsCancellationRequested);
                for (int i = 0; i < RequestCount; i++)
                {
                    using (responses[i])
                        Assert.AreEqual(HttpStatusCode.OK, responses[i].StatusCode, "request " + i);
                }
            });
        }

        // /a -> /b -> /c (200).
        private static UHttpResponse Respond(UHttpRequest req, RequestContext ctx)
        {
            string next = req.Uri.AbsolutePath == "/a" ? "/b" : req.Uri.AbsolutePath == "/b" ? "/c" : null;
            if (next == null)
                return new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req);

            var headers = new HttpHeaders();
            headers.Set("Location", next);
            return new UHttpResponse(HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req);
        }

        private static int CountEvents(RequestContext context, string name)
        {
            int count = 0;
            var timeline = context.Timeline;
            for (int i = 0; i < timeline.Count; i++)
            {
                if (timeline[i].Name == name)
                    count++;
            }

            return count;
        }
    }
}
