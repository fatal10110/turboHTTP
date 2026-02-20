using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityStressSuiteTests
    {
        [UnityTest]
        public System.Collections.IEnumerator MainThreadDispatcher_MetricsRemainBoundedUnderBurst()
        {
            var defaults = MainThreadDispatcher.GetSettings();
            MainThreadDispatcher.Configure(new MainThreadDispatcherSettings
            {
                UserQueueCapacity = 64,
                BackpressurePolicy = MainThreadBackpressurePolicy.Reject,
                MaxPendingWaiters = 32,
                WaitTimeout = System.TimeSpan.FromMilliseconds(10),
                MaxItemsPerFrame = 16,
                MaxWorkTimeMs = 0.5,
                LowMemoryDropCount = 8,
                AllowInlineExecutionOnMainThread = false
            });

            try
            {
                var _ = MainThreadDispatcher.Instance;
                var tasks = new Task[256];
                for (var i = 0; i < tasks.Length; i++)
                {
                    tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { }));
                }

                var all = Task.WhenAll(tasks);
                yield return new WaitUntil(() => all.IsCompleted);

                var metrics = MainThreadDispatcher.GetMetrics();
                Assert.LessOrEqual(metrics.Queue.UserQueueDepth, 64);
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }
    }
}
