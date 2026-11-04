using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Unity.Mobile;
using TurboHTTP.Unity.Mobile.Android;
using TurboHTTP.Unity.Mobile.iOS;

namespace TurboHTTP.Tests.Mobile
{
    /// <summary>
    /// The background bridges' managed logic over fake native layers (#87, #88): begin/end run
    /// synchronously on the calling thread (no MainThreadDispatcher, which is not ticking in these
    /// tests), a foreground scope is never expired by a timer, and only the native expiration handler
    /// expires a scope.
    /// </summary>
    [TestFixture]
    public class BackgroundBridgeNativeTests
    {
        [TestCase(double.MaxValue)]
        [TestCase(double.PositiveInfinity)]
        [TestCase(double.NaN)]
        [TestCase(-1.0)]
        [TestCase(1.0e7)]
        public void IosRemainingTime_ForegroundOrUnreadable_IsUnlimited(double seconds)
        {
            Assert.AreEqual(TimeSpan.MaxValue, IosBackgroundTaskBindings.ToRemainingBudget(seconds));
        }

        [TestCase(0.0, 0L)]
        [TestCase(29.5, 295000000L)]
        [TestCase(180.0, 1800000000L)]
        public void IosRemainingTime_BackgroundBudget_IsExact(double seconds, long ticks)
        {
            Assert.AreEqual(TimeSpan.FromTicks(ticks), IosBackgroundTaskBindings.ToRemainingBudget(seconds));
        }

        [Test]
        public void IosAcquire_BeginsOnCallingThread_WithoutDispatcher()
        {
            RunOnWorker(() =>
            {
                var native = new FakeIosNative { RemainingSeconds = double.MaxValue };
                var bridge = new IosBackgroundTaskBridge(native);

                var pending = bridge.AcquireAsync(NewContext(), CancellationToken.None);

                Assert.IsTrue(pending.IsCompleted, "begin must not wait for the main thread");
                var scope = pending.Result;
                Assert.IsNotNull(scope);
                Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, native.BeginThreadId);

                var dispose = scope.DisposeAsync();
                Assert.IsTrue(dispose.IsCompleted, "end must not wait for the main thread");
                Assert.AreEqual(1, native.EndCalls);
                Assert.AreEqual(0, native.ActiveCount);
            });
        }

        [Test]
        public void IosForegroundScope_IsNotExpiredAndReportsUnlimitedBudget()
        {
            var native = new FakeIosNative { RemainingSeconds = double.MaxValue };
            var scope = Acquire(new IosBackgroundTaskBridge(native));

            Assert.IsFalse(scope.ExpirationToken.IsCancellationRequested);
            Assert.AreEqual(TimeSpan.MaxValue, scope.RemainingBudget);
            scope.DisposeAsync().GetAwaiter().GetResult();
        }

        [Test]
        public void IosNativeExpiration_CancelsScopeAndEndsTaskOnce()
        {
            var native = new FakeIosNative { RemainingSeconds = double.MaxValue };
            var scope = Acquire(new IosBackgroundTaskBridge(native));

            native.ExpireAll();

            Assert.IsTrue(scope.ExpirationToken.IsCancellationRequested);
            Assert.AreEqual(TimeSpan.Zero, scope.RemainingBudget);
            scope.DisposeAsync().GetAwaiter().GetResult();
            Assert.AreEqual(1, native.TasksEnded, "the native task is ended exactly once");
        }

        [Test]
        public void IosNativeExpiration_QueuesTheInFlightRequest()
        {
            Task.Run(async () =>
            {
                var native = new FakeIosNative { RemainingSeconds = double.MaxValue };
                var interceptor = new BackgroundNetworkingInterceptor(
                    new BackgroundNetworkingPolicy { Enable = true, QueueOnAppPause = true },
                    new IosBackgroundTaskBridge(native));
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/expire"));

                await TestHelpers.AssertThrowsAsync<BackgroundRequestQueuedException>(async () =>
                {
                    await TransportDispatchHelper.CollectResponseAsync(
                        interceptor.Wrap((req, handler, ctx, ct) =>
                        {
                            native.ExpireAll();
                            ct.ThrowIfCancellationRequested();
                            return Task.CompletedTask;
                        }),
                        request,
                        new RequestContext(request),
                        CancellationToken.None);
                });

                Assert.AreEqual(1, interceptor.Queued);
                Assert.AreEqual(1, native.TasksEnded);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void IosBeginFailure_FallsBackToForeground()
        {
            Assert.IsNull(Acquire(new IosBackgroundTaskBridge(new FakeIosNative { BeginResult = false })));
            Assert.IsNull(Acquire(new IosBackgroundTaskBridge(new FakeIosNative { ThrowOnBegin = true })));
            Assert.IsNull(Acquire(new IosBackgroundTaskBridge((IIosBackgroundTaskNative)null)));
        }

        [Test]
        public void AndroidAcquireAndEnqueue_CallPluginOnCallingThread_WithoutDispatcher()
        {
            RunOnWorker(() =>
            {
                var plugin = new FakeAndroidPlugin();
                var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin);
                var thread = Thread.CurrentThread.ManagedThreadId;

                var pending = bridge.AcquireAsync(NewContext(), CancellationToken.None);
                Assert.IsTrue(pending.IsCompleted, "begin must not wait for the main thread");
                var scope = pending.Result;
                Assert.IsNotNull(scope);
                Assert.IsTrue(scope.DisposeAsync().IsCompleted, "end must not wait for the main thread");

                Assert.IsTrue(bridge.TryEnqueueDeferredWork("key"));
                Assert.IsTrue(bridge.TryMarkReplayComplete("key"));

                CollectionAssert.AreEqual(
                    new[] { "init", "begin", "end", "init", "enqueue:key", "cancel:key" },
                    plugin.Calls);
                CollectionAssert.AreEqual(new[] { thread }, plugin.Threads);
            });
        }

        [Test]
        public void AndroidPluginFailure_FallsBackAndReleasesTheKey()
        {
            var plugin = new FakeAndroidPlugin { Initialized = false };
            var bridge = new AndroidBackgroundWorkBridge(new AndroidBackgroundWorkConfig(), plugin);

            Assert.IsNull(Acquire(bridge));
            Assert.IsFalse(bridge.TryEnqueueDeferredWork("key"));

            plugin.Initialized = true;
            plugin.ThrowOnEnqueue = true;
            Assert.IsFalse(bridge.TryEnqueueDeferredWork("key"));

            plugin.ThrowOnEnqueue = false;
            Assert.IsTrue(bridge.TryEnqueueDeferredWork("key"), "a failed enqueue must not keep the dedupe key");
        }

        private static IBackgroundExecutionScope Acquire(IBackgroundExecutionBridge bridge)
        {
            return bridge.AcquireAsync(NewContext(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        private static RequestContext NewContext()
        {
            return new RequestContext(new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/bg")));
        }

        private static void RunOnWorker(Action body)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });
            thread.Start();
            thread.Join();
            if (failure != null)
                throw failure;
        }

        private sealed class FakeIosNative : IIosBackgroundTaskNative
        {
            private readonly object _gate = new object();
            private readonly HashSet<int> _active = new HashSet<int>();

            public bool BeginResult { get; set; } = true;
            public bool ThrowOnBegin { get; set; }
            public double RemainingSeconds { get; set; }
            public int BeginThreadId { get; private set; }
            public int EndCalls { get; private set; }
            public int TasksEnded { get; private set; }

            public int ActiveCount
            {
                get
                {
                    lock (_gate)
                        return _active.Count;
                }
            }

            public bool Begin(string taskName, int cookie)
            {
                BeginThreadId = Thread.CurrentThread.ManagedThreadId;
                if (ThrowOnBegin)
                    throw new EntryPointNotFoundException("turbohttp_begin_background_task");
                if (!BeginResult)
                    return false;

                lock (_gate)
                    _active.Add(cookie);
                return true;
            }

            public void End(int cookie)
            {
                EndCalls++;
                lock (_gate)
                {
                    if (_active.Remove(cookie))
                        TasksEnded++;
                }
            }

            public double GetRemainingSeconds()
            {
                return RemainingSeconds;
            }

            // Mirrors the native expiration handler: take the task, signal managed code, end the task.
            public void ExpireAll()
            {
                int[] cookies;
                lock (_gate)
                {
                    cookies = new int[_active.Count];
                    _active.CopyTo(cookies);
                    _active.Clear();
                }

                foreach (var cookie in cookies)
                {
                    IosBackgroundTaskBridge.HandleNativeExpiration(cookie);
                    TasksEnded++;
                }
            }
        }

        private sealed class FakeAndroidPlugin : IAndroidBackgroundPlugin
        {
            public bool Initialized { get; set; } = true;
            public bool ThrowOnEnqueue { get; set; }
            public List<string> Calls { get; } = new List<string>();
            public List<int> Threads { get; } = new List<int>();

            public bool EnsureInitialized()
            {
                Record("init");
                return Initialized;
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
                if (ThrowOnEnqueue)
                    throw new InvalidOperationException("JNI failure");
                return true;
            }

            public void CancelDeferredWork(string dedupeKey)
            {
                Record("cancel:" + dedupeKey);
            }

            private void Record(string call)
            {
                Calls.Add(call);
                var thread = Thread.CurrentThread.ManagedThreadId;
                if (!Threads.Contains(thread))
                    Threads.Add(thread);
            }
        }
    }
}
