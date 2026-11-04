using System;
using System.Collections.Generic;
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
    public partial class CacheInterceptorTests
    {
        private static readonly TimeSpan BackgroundContextBoundedWait = TimeSpan.FromSeconds(10);

        // Regression for #245: the background cache store must never resume on the caller's
        // SynchronizationContext. The test thread installs a context that never pumps (Unity's
        // main thread blocked in GetAwaiter().GetResult() behaves the same way). A 200
        // revalidation waits for the pending store of the same base key, so a store continuation
        // posted to that context would hang the foreground request forever.
        [Test]
        public void CacheInterceptor_Revalidation200_DoesNotWaitOnCallerSynchronizationContext()
        {
            var storage = new ContextRecordingCacheStorage(expectedSets: 2);
            var middleware = new CacheInterceptor(new CachePolicy { Storage = storage });

            int callCount = 0;
            string revalidationIfNoneMatch = null;
            var transport = new MockTransport((req, ctx, ct) =>
            {
                var call = Interlocked.Increment(ref callCount);
                if (call == 2)
                    Volatile.Write(ref revalidationIfNoneMatch, req.Headers.Get("If-None-Match"));

                var headers = new HttpHeaders();
                headers.Set("Cache-Control", "no-cache");
                headers.Set("ETag", call == 1 ? "\"v1\"" : "\"v2\"");
                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK,
                    headers,
                    Encoding.UTF8.GetBytes(call == 1 ? "v1" : "v2"),
                    ctx.Elapsed,
                    req));
            });

            var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/sync-context"));
            var callerContext = new CountingNonPumpingSynchronizationContext();
            var priorContext = SynchronizationContext.Current;
            Task<UHttpResponse> primed;
            Task<UHttpResponse> revalidated;
            bool primedCompleted;
            bool revalidatedCompleted;
            bool storesCompleted;
            try
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);

                // Called directly (not through Task.Run) so the library sees the non-pumping
                // context as the caller's context.
                primed = pipeline.ExecuteAsync(request, new RequestContext(request));
                primedCompleted = primed.Wait(BackgroundContextBoundedWait);

                revalidated = pipeline.ExecuteAsync(request, new RequestContext(request));
                revalidatedCompleted = revalidated.Wait(BackgroundContextBoundedWait);

                storesCompleted = storage.AllSetsObserved.Wait(BackgroundContextBoundedWait);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(priorContext);
            }

            try
            {
                Assert.IsTrue(primedCompleted, "Priming request did not complete.");
                Assert.IsTrue(
                    revalidatedCompleted,
                    "The 200 revalidation hung waiting on a cache store whose continuation was posted to " +
                    "the caller's non-pumping SynchronizationContext.");
                Assert.IsTrue(storesCompleted, "Background cache stores did not run.");

                Assert.AreEqual(2, Volatile.Read(ref callCount));
                Assert.AreEqual("\"v1\"", Volatile.Read(ref revalidationIfNoneMatch));

                var response = revalidated.GetAwaiter().GetResult();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("v2", response.GetBodyAsString());

                var observed = storage.SnapshotObservedContexts();
                Assert.AreEqual(2, observed.Count);
                for (int i = 0; i < observed.Count; i++)
                {
                    Assert.IsFalse(
                        ReferenceEquals(observed[i], callerContext),
                        "Background cache store ran on the caller's SynchronizationContext.");
                }

                Assert.AreEqual(
                    0,
                    callerContext.PostCount,
                    "Library work was posted to the caller's SynchronizationContext.");

                response.Dispose();
                primed.GetAwaiter().GetResult().Dispose();
            }
            finally
            {
                middleware.Dispose();
                request.Dispose();
            }
        }

        private sealed class CountingNonPumpingSynchronizationContext : SynchronizationContext
        {
            private int _postCount;

            internal int PostCount => Volatile.Read(ref _postCount);

            public override void Post(SendOrPostCallback d, object state)
            {
                // Never runs the callback: models a main thread that is blocked and not pumping.
                Interlocked.Increment(ref _postCount);
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                Interlocked.Increment(ref _postCount);
            }
        }

        private sealed class ContextRecordingCacheStorage : ICacheStorage
        {
            private readonly MemoryCacheStorage _inner = new MemoryCacheStorage();
            private readonly object _gate = new object();
            private readonly List<SynchronizationContext> _observedContexts = new List<SynchronizationContext>();
            private readonly TaskCompletionSource<object> _allSetsObserved =
                new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly int _expectedSets;

            internal ContextRecordingCacheStorage(int expectedSets)
            {
                _expectedSets = expectedSets;
            }

            internal Task AllSetsObserved => _allSetsObserved.Task;

            internal List<SynchronizationContext> SnapshotObservedContexts()
            {
                lock (_gate)
                {
                    return new List<SynchronizationContext>(_observedContexts);
                }
            }

            public Task<CacheEntry> GetAsync(string key, CancellationToken cancellationToken = default)
            {
                return _inner.GetAsync(key, cancellationToken);
            }

            public async Task SetAsync(string key, CacheEntry entry, CancellationToken cancellationToken = default)
            {
                var current = SynchronizationContext.Current;
                await _inner.SetAsync(key, entry, cancellationToken).ConfigureAwait(false);

                bool reached;
                lock (_gate)
                {
                    _observedContexts.Add(current);
                    reached = _observedContexts.Count >= _expectedSets;
                }

                if (reached)
                    _allSetsObserved.TrySetResult(null);
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
    }
}
