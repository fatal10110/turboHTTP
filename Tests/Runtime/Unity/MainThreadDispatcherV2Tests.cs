using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class MainThreadDispatcherV2Tests
    {
        [UnityTest]
        public System.Collections.IEnumerator ExecuteAsync_RejectPolicy_BoundsQueueUnderSaturation()
        {
            var defaults = MainThreadDispatcher.GetSettings();

            MainThreadDispatcher.Configure(new MainThreadDispatcherSettings
            {
                UserQueueCapacity = 1,
                BackpressurePolicy = MainThreadBackpressurePolicy.Reject,
                MaxPendingWaiters = 8,
                WaitTimeout = TimeSpan.FromMilliseconds(10),
                MaxItemsPerFrame = 1,
                MaxWorkTimeMs = 0.1,
                LowMemoryDropCount = 1,
                AllowInlineExecutionOnMainThread = false
            });

            try
            {
                var _ = MainThreadDispatcher.Instance;
                var rejected = 0;

                var tasks = new Task[64];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(async () =>
                    {
                        try
                        {
                            await MainThreadDispatcher.ExecuteAsync(() => { });
                        }
                        catch (InvalidOperationException)
                        {
                            Interlocked.Increment(ref rejected);
                        }
                    });
                }

                var all = Task.WhenAll(tasks);
                yield return new WaitUntil(() => all.IsCompleted);

                Assert.Greater(rejected, 0, "Expected queue saturation to reject some enqueues.");

                var metrics = MainThreadDispatcher.GetMetrics();
                Assert.Greater(metrics.Queue.RejectedItems, 0);
                Assert.LessOrEqual(metrics.Queue.UserQueueDepth, 1);
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator ExecuteControlAsync_RemainsRoutableDuringUserBackpressure()
        {
            var defaults = MainThreadDispatcher.GetSettings();

            MainThreadDispatcher.Configure(new MainThreadDispatcherSettings
            {
                UserQueueCapacity = 1,
                BackpressurePolicy = MainThreadBackpressurePolicy.Reject,
                MaxPendingWaiters = 8,
                WaitTimeout = TimeSpan.FromMilliseconds(10),
                MaxItemsPerFrame = 1,
                MaxWorkTimeMs = 0.1,
                LowMemoryDropCount = 1,
                AllowInlineExecutionOnMainThread = false
            });

            try
            {
                var _ = MainThreadDispatcher.Instance;

                var saturators = new Task[32];
                for (var i = 0; i < saturators.Length; i++)
                {
                    saturators[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { }));
                }

                var controlRan = false;
                var controlTask = Task.Run(async () =>
                {
                    await MainThreadDispatcher.ExecuteControlAsync(() => controlRan = true);
                });

                yield return new WaitUntil(() => controlTask.IsCompleted);

                Assert.AreEqual(TaskStatus.RanToCompletion, controlTask.Status);
                Assert.IsTrue(controlRan);
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }
    }
}
