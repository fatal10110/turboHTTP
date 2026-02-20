using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity.Mobile.Android;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class BackgroundNetworkingTests
    {
        [Test]
        public void PolicyDisabled_NoBehaviorChange()
        {
            Task.Run(async () =>
            {
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = false
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                var context = new RequestContext(request);
                var transport = new MockTransport();

                var response = await middleware.InvokeAsync(
                    request,
                    context,
                    (req, ctx, ct) => transport.SendAsync(req, ctx, ct),
                    CancellationToken.None);

                Assert.IsTrue(response.IsSuccessStatusCode);
                Assert.AreEqual(0, middleware.Queued);
                Assert.AreEqual(0, middleware.Expired);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ReplayUnsafeBody_Rejected()
        {
            Task.Run(async () =>
            {
                var bridge = new FakeBackgroundExecutionBridge(expireImmediately: true);
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = true,
                    QueueOnAppPause = true,
                    RequireReplayableBodyForQueue = true
                }, bridge);

                var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/post"), body: null);
                var context = new RequestContext(request);

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        (req, ctx, ct) => throw new OperationCanceledException(ct),
                        CancellationToken.None);
                });

                Assert.AreEqual(0, middleware.Queued);
                Assert.AreEqual(1, middleware.Dropped);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NoBridge_Cancellation_DoesNotQueue()
        {
            Task.Run(async () =>
            {
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = true,
                    QueueOnAppPause = true,
                    RequireReplayableBodyForQueue = true
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/cancel-no-bridge"));
                var context = new RequestContext(request);

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        (req, ctx, ct) => throw new OperationCanceledException(ct),
                        CancellationToken.None);
                });

                Assert.AreEqual(0, middleware.Queued);
                Assert.AreEqual(0, middleware.Expired);
                Assert.AreEqual(0, middleware.Dropped);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void IosExpiration_TriggersQueue()
        {
            Task.Run(async () =>
            {
                var bridge = new FakeBackgroundExecutionBridge(expireImmediately: true);
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = true,
                    QueueOnAppPause = true,
                    RequireReplayableBodyForQueue = true
                }, bridge);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/get"));
                var context = new RequestContext(request);

                var queued = await TestHelpers.AssertThrowsAsync<BackgroundRequestQueuedException>(async () =>
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        (req, ctx, ct) => throw new OperationCanceledException(ct),
                        CancellationToken.None);
                });

                Assert.AreEqual("GET:https://example.test/get", queued.ReplayDedupeKey);
                Assert.AreEqual(1, middleware.Queued);
                Assert.AreEqual(1, middleware.Expired);
                Assert.IsTrue(middleware.TryDequeueReplayable(out var replay));
                Assert.AreEqual(HttpMethod.GET, replay.Method);
                Assert.AreEqual(1, middleware.Replayed);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void UserCancellation_DoesNotQueueWhenScopeActive()
        {
            Task.Run(async () =>
            {
                var bridge = new FakeBackgroundExecutionBridge(expireImmediately: false);
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = true,
                    QueueOnAppPause = true,
                    RequireReplayableBodyForQueue = true
                }, bridge);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/cancel"));
                var context = new RequestContext(request);
                using var cts = new CancellationTokenSource();

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        async (req, ctx, ct) =>
                        {
                            cts.Cancel();
                            ct.ThrowIfCancellationRequested();
                            await Task.Yield();
                            return new UHttpResponse(System.Net.HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req);
                        },
                        cts.Token);
                });

                Assert.AreEqual(0, middleware.Queued);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void AndroidQueue_DeduplicatesWorkId()
        {
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig
            {
                EnableDeferredWork = true,
                MaxQueuedRequests = 10
            });

            Assert.IsTrue(bridge.TryEnqueueDeferredWork("same-key"));
            Assert.IsFalse(bridge.TryEnqueueDeferredWork("same-key"));
            Assert.IsTrue(bridge.TryMarkReplayComplete("same-key"));
            Assert.IsTrue(bridge.TryEnqueueDeferredWork("same-key"));
        }

        [Test]
        public void DeferredBridge_ReceivesEnqueueAndReplaySignals()
        {
            Task.Run(async () =>
            {
                var bridge = new FakeDeferredBridge();
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = true,
                    QueueOnAppPause = true
                }, bridge);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://example.test/deferred"),
                    metadata: new System.Collections.Generic.Dictionary<string, object>
                    {
                        [RequestMetadataKeys.BackgroundReplayDedupeKey] = "replay-key"
                    });

                var context = new RequestContext(request);

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await middleware.InvokeAsync(
                        request,
                        context,
                        (req, ctx, ct) => throw new OperationCanceledException(ct),
                        CancellationToken.None);
                });

                Assert.AreEqual(1, bridge.EnqueueCalls);
                Assert.IsTrue(middleware.TryDequeueReplayable(out _));
                Assert.AreEqual(1, bridge.CompleteCalls);
            }).GetAwaiter().GetResult();
        }

        private sealed class FakeBackgroundExecutionBridge : IBackgroundExecutionBridge
        {
            private readonly bool _expireImmediately;

            public FakeBackgroundExecutionBridge(bool expireImmediately)
            {
                _expireImmediately = expireImmediately;
            }

            public ValueTask<IBackgroundExecutionScope> AcquireAsync(
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                if (_expireImmediately)
                    cts.Cancel();

                IBackgroundExecutionScope scope = new FakeScope(cts);
                return new ValueTask<IBackgroundExecutionScope>(scope);
            }
        }

        private sealed class FakeScope : IBackgroundExecutionScope
        {
            private readonly CancellationTokenSource _cts;

            public FakeScope(CancellationTokenSource cts)
            {
                _cts = cts;
                ScopeId = Guid.NewGuid().ToString("N");
                StartedAtUtc = DateTime.UtcNow;
            }

            public string ScopeId { get; }
            public DateTime StartedAtUtc { get; }
            public TimeSpan RemainingBudget => TimeSpan.FromSeconds(10);
            public CancellationToken ExpirationToken => _cts.Token;

            public ValueTask DisposeAsync()
            {
                _cts.Cancel();
                _cts.Dispose();
                return default;
            }
        }

        private sealed class FakeDeferredBridge : IBackgroundExecutionBridge, IDeferredBackgroundWorkBridge
        {
            public int EnqueueCalls { get; private set; }
            public int CompleteCalls { get; private set; }

            public ValueTask<IBackgroundExecutionScope> AcquireAsync(
                RequestContext context,
                CancellationToken cancellationToken)
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.Cancel();
                IBackgroundExecutionScope scope = new FakeScope(cts);
                return new ValueTask<IBackgroundExecutionScope>(scope);
            }

            public bool TryEnqueueDeferredWork(string dedupeKey)
            {
                EnqueueCalls++;
                return true;
            }

            public bool TryMarkReplayComplete(string dedupeKey)
            {
                CompleteCalls++;
                return true;
            }
        }
    }
}
