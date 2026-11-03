using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile;
using TurboHTTP.Unity.Mobile.Android;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// Android background scopes expire on a bridge-owned signal (#267): an expired replayable
    /// request reaches WorkManager, a non-replayable one fails as in the foreground. Fake plugin and
    /// fake signal only; the device check is in #266.
    /// </summary>
    [TestFixture]
    public class AndroidBackgroundExpiryTests
    {
        [SetUp]
        public void SetUp()
        {
            AndroidAppPauseSource.ResetStaticState();
        }

        [TearDown]
        public void TearDown()
        {
            AndroidAppPauseSource.ResetStaticState();
        }

        [Test]
        public void ExpiredScope_ReplayableRequest_IsQueuedAndHandedToWorkManager()
        {
            Task.Run(async () =>
            {
                var plugin = new FakeAndroidPlugin();
                var signal = new FakeExpirySignal();
                var interceptor = new BackgroundNetworkingInterceptor(
                    new BackgroundNetworkingPolicy { Enable = true, QueueOnAppPause = true },
                    new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin, signal));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expire"));

                await TestHelpers.AssertThrowsAsync<BackgroundRequestQueuedException>(async () =>
                {
                    await TransportDispatchHelper.CollectResponseAsync(
                        interceptor.Wrap((req, handler, ctx, ct) =>
                        {
                            signal.SetExpired(true);
                            ct.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                        }),
                        request,
                        new RequestContext(request),
                        CancellationToken.None);
                });

                Assert.AreEqual(1, interceptor.Queued);
                Assert.AreEqual(1, interceptor.Expired);
                CollectionAssert.Contains(plugin.Calls, "enqueue:GET:https://example.test/expire");
                CollectionAssert.Contains(plugin.Calls, "end");
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ExpiredScope_NonReplayableRequest_FailsAndIsNotHandedToWorkManager()
        {
            Task.Run(async () =>
            {
                var plugin = new FakeAndroidPlugin();
                var signal = new FakeExpirySignal();
                var interceptor = new BackgroundNetworkingInterceptor(
                    new BackgroundNetworkingPolicy
                    {
                        Enable = true,
                        QueueOnAppPause = true,
                        RequireReplayableBodyForQueue = true
                    },
                    new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin, signal));

                using var stream = new NonSeekableStream(Encoding.UTF8.GetBytes("post"));
                using var request = new UHttpRequest(HttpMethod.POST, new Uri("https://example.test/post"))
                    .WithStreamBody(stream, contentLength: 4, leaveOpen: true);

                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () =>
                {
                    await TransportDispatchHelper.CollectResponseAsync(
                        interceptor.Wrap((req, handler, ctx, ct) =>
                        {
                            signal.SetExpired(true);
                            ct.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                        }),
                        request,
                        new RequestContext(request),
                        CancellationToken.None);
                });

                Assert.AreEqual(0, interceptor.Queued);
                Assert.AreEqual(1, interceptor.Dropped);
                Assert.IsFalse(plugin.Calls.Exists(call => call.StartsWith("enqueue:", StringComparison.Ordinal)));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ScopeToken_IsCancelledOnExpiry_AndLaterScopesGetAFreshTokenAfterResume()
        {
            var signal = new FakeExpirySignal();
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), new FakeAndroidPlugin(), signal);

            var foreground = Acquire(bridge);
            Assert.IsFalse(foreground.ExpirationToken.IsCancellationRequested);

            signal.SetExpired(true);
            Assert.IsTrue(foreground.ExpirationToken.IsCancellationRequested);
            Assert.AreEqual(TimeSpan.Zero, foreground.RemainingBudget);

            var whileExpired = Acquire(bridge);
            Assert.IsTrue(whileExpired.ExpirationToken.IsCancellationRequested, "a scope begun after expiry is expired");

            signal.SetExpired(false);
            var resumed = Acquire(bridge);
            Assert.IsFalse(resumed.ExpirationToken.IsCancellationRequested);

            signal.SetExpired(true);
            Assert.IsTrue(resumed.ExpirationToken.IsCancellationRequested);
        }

        [Test]
        public void ScopeWithoutSignal_NeverExpires()
        {
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), new FakeAndroidPlugin());
            var scope = Acquire(bridge);

            Assert.IsFalse(scope.ExpirationToken.CanBeCanceled);
        }

        [Test]
        public void PauseSignal_ZeroBudget_ExpiresOnPause_AndClearsOnResume()
        {
            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 0);
            var raised = 0;
            signal.ExpiryChanged += () => raised++;

            Assert.IsFalse(signal.IsExpired);
            Assert.AreEqual(TimeSpan.Zero, signal.RemainingBudget);

            AndroidAppPauseSource.Report(true);
            Assert.IsTrue(signal.IsExpired);
            Assert.AreEqual(TimeSpan.Zero, signal.RemainingBudget);

            AndroidAppPauseSource.Report(false);
            Assert.IsFalse(signal.IsExpired);
            Assert.AreEqual(2, raised);
        }

        [Test]
        public void PauseSignal_BudgetNotSpent_IsNotExpired_AndResumeCancelsTheTimer()
        {
            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 60 * 60 * 1000);
            var raised = 0;
            signal.ExpiryChanged += () => raised++;

            Assert.AreEqual(TimeSpan.FromHours(1), signal.RemainingBudget, "the foreground budget is the whole budget");

            AndroidAppPauseSource.Report(true);
            Assert.IsFalse(signal.IsExpired);
            Assert.That(signal.RemainingBudget, Is.LessThanOrEqualTo(TimeSpan.FromHours(1)));
            Assert.That(signal.RemainingBudget, Is.GreaterThan(TimeSpan.Zero));

            AndroidAppPauseSource.Report(false);
            Assert.IsFalse(signal.IsExpired);
            Assert.AreEqual(0, raised);
        }

        [Test]
        public void PauseSignal_CreatedWhilePaused_StartsTheBudget()
        {
            AndroidAppPauseSource.Report(true);

            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 0);

            Assert.IsTrue(signal.IsExpired);
        }

        [Test]
        public void Bridge_IsReachableFromThePauseSource_OnlyWhileItHasLiveScopes()
        {
            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 60 * 60 * 1000);
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), new FakeAndroidPlugin(), signal);
            Assert.AreEqual(0, AndroidAppPauseSource.ListenerCount, "an idle bridge is not rooted by the static source");

            var first = Acquire(bridge);
            var second = Acquire(bridge);
            Assert.AreEqual(1, AndroidAppPauseSource.ListenerCount);

            Release(first);
            Assert.AreEqual(1, AndroidAppPauseSource.ListenerCount, "a live scope keeps the signal attached");

            Release(second);
            Release(second);
            Assert.AreEqual(0, AndroidAppPauseSource.ListenerCount, "the last scope detaches it, once");

            var third = Acquire(bridge);
            Assert.AreEqual(1, AndroidAppPauseSource.ListenerCount);
            Release(third);
            Assert.AreEqual(0, AndroidAppPauseSource.ListenerCount);
        }

        [Test]
        public void Bridge_ScopeBegunAfterAnExpiryNobodyListenedTo_IsExpired()
        {
            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 0);
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), new FakeAndroidPlugin(), signal);

            var foreground = Acquire(bridge);
            Release(foreground);

            AndroidAppPauseSource.Report(true);
            var whilePaused = Acquire(bridge);
            Assert.IsTrue(whilePaused.ExpirationToken.IsCancellationRequested, "the budget ran from the pause, not from the attach");
            Release(whilePaused);

            AndroidAppPauseSource.Report(false);
            var resumed = Acquire(bridge);
            Assert.IsFalse(resumed.ExpirationToken.IsCancellationRequested, "a resume nobody listened to still clears the expiry");

            AndroidAppPauseSource.Report(true);
            Assert.IsTrue(resumed.ExpirationToken.IsCancellationRequested);
            Release(resumed);
        }

        [Test]
        public void PauseSignal_AttachedMidPause_TimesTheBudgetFromThePause()
        {
            AndroidAppPauseSource.Report(true);
            var signal = new AndroidPauseExpirySignal(budgetMilliseconds: 60 * 60 * 1000);
            Assert.That(signal.RemainingBudget, Is.LessThanOrEqualTo(TimeSpan.FromHours(1)));

            Action handler = () => { };
            signal.ExpiryChanged += handler;
            Assert.AreEqual(1, AndroidAppPauseSource.ListenerCount);
            Assert.IsFalse(signal.IsExpired);

            signal.ExpiryChanged -= handler;
            Assert.AreEqual(0, AndroidAppPauseSource.ListenerCount);
        }

        [Test]
        public void Config_Clone_CopiesTheBudget()
        {
            var clone = new AndroidBackgroundWorkConfig { BackgroundBudgetMilliseconds = 1234 }.Clone();

            Assert.AreEqual(1234, clone.BackgroundBudgetMilliseconds);
            Assert.AreEqual(5000, new AndroidBackgroundWorkConfig().BackgroundBudgetMilliseconds);
        }

        private static IBackgroundExecutionScope Acquire(AndroidBackgroundWorkBridge bridge)
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/scope"));
            var pending = bridge.AcquireAsync(new RequestContext(request), CancellationToken.None);
            Assert.IsTrue(pending.IsCompleted);
            Assert.IsNotNull(pending.Result);
            return pending.Result;
        }

        private static void Release(IBackgroundExecutionScope scope)
        {
            // The bridge's dispose action is synchronous.
            Assert.IsTrue(scope.DisposeAsync().IsCompleted);
        }

        private sealed class FakeExpirySignal : IAndroidBackgroundExpirySignal
        {
            private volatile bool _expired;

            public bool IsExpired => _expired;

            public TimeSpan RemainingBudget => _expired ? TimeSpan.Zero : TimeSpan.FromSeconds(5);

            public event Action ExpiryChanged;

            public void SetExpired(bool expired)
            {
                _expired = expired;
                ExpiryChanged?.Invoke();
            }
        }

        private sealed class FakeAndroidPlugin : IAndroidBackgroundPlugin
        {
            private readonly object _gate = new object();

            public List<string> Calls { get; } = new List<string>();

            public bool EnsureInitialized()
            {
                Record("init");
                return true;
            }

            public bool BeginGuard(string scopeId, int graceMilliseconds)
            {
                Record("begin");
                return true;
            }

            public void EndGuard(string scopeId)
            {
                Record("end");
            }

            public bool EnqueueDeferredWork(string dedupeKey)
            {
                Record("enqueue:" + dedupeKey);
                return true;
            }

            public void CancelDeferredWork(string dedupeKey)
            {
                Record("cancel:" + dedupeKey);
            }

            private void Record(string call)
            {
                lock (_gate)
                {
                    Calls.Add(call);
                }
            }
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] buffer)
                : base(buffer, writable: false)
            {
            }

            public override bool CanSeek => false;
        }
    }
}
