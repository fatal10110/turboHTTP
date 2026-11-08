using System;
using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace TurboHTTP.Tests.UnityModule
{
    /// <summary>
    /// Phase 15.6 performance budgets (#105): queue depth under synthetic load and the temp-file
    /// manager's counters. The frame-time and leaked-temp-file budgets of the texture and audio bursts
    /// run in <see cref="UnityStressSuiteTests"/>; every budget prints a metric line
    /// (<see cref="ReliabilityBudgets"/>).
    /// </summary>
    public class UnityPerformanceBudgetTests
    {
        [Test]
        public void DispatcherBudget_GuardsQueueDepthAgainstConfiguredCap()
        {
            var settings = MainThreadDispatcher.GetSettings();
            var metrics = MainThreadDispatcher.GetMetrics();

            ReliabilityBudgets.Current.AssertWithin(
                "dispatcher.idle_queue_depth",
                metrics.Queue.UserQueueDepth,
                settings.UserQueueCapacity,
                "items");
        }

        [UnityTest]
        public IEnumerator DispatcherBudget_QueueDepthUnderSyntheticLoad_StaysWithinCap()
        {
            Assert.IsNotNull(MainThreadDispatcher.Instance);
            const int capacity = 32;
            const int producers = 256;
            var budgets = ReliabilityBudgets.Current;
            var defaults = MainThreadDispatcher.GetSettings();
            var settings = defaults.Clone();
            settings.UserQueueCapacity = capacity;
            settings.BackpressurePolicy = MainThreadBackpressurePolicy.Reject;
            settings.MaxItemsPerFrame = 8;
            settings.AllowInlineExecutionOnMainThread = false;
            MainThreadDispatcher.Configure(settings);

            try
            {
                var rejectedBefore = MainThreadDispatcher.GetMetrics().Queue.RejectedItems;
                var tasks = new Task[producers];
                for (var i = 0; i < producers; i++)
                    tasks[i] = Task.Run(() => MainThreadDispatcher.ExecuteAsync(() => { }));

                // Sampled once per frame, at the start of the frame before the drain.
                var maxDepth = 0;
                var all = Task.WhenAll(tasks).ContinueWith(_ => { }, TaskScheduler.Default);
                yield return new WaitUntil(() =>
                {
                    maxDepth = Math.Max(maxDepth, MainThreadDispatcher.GetMetrics().Queue.UserQueueDepth);
                    return all.IsCompleted;
                });

                var completed = 0;
                var rejected = 0;
                for (var i = 0; i < producers; i++)
                {
                    if (tasks[i].Status == TaskStatus.RanToCompletion)
                        completed++;
                    else if (tasks[i].IsFaulted && tasks[i].Exception?.GetBaseException() is MainThreadDispatchRejectedException)
                        rejected++;
                }

                Assert.AreEqual(producers, completed + rejected, "every producer either ran or was rejected by backpressure");
                Assert.AreEqual(rejected, MainThreadDispatcher.GetMetrics().Queue.RejectedItems - rejectedBefore, "rejections are counted");
                budgets.Record("dispatcher.rejected_under_load", rejected, "items");
                budgets.AssertWithin("dispatcher.max_queue_depth", maxDepth, capacity, "items");
            }
            finally
            {
                MainThreadDispatcher.Configure(defaults);
            }
        }

        [Test]
        public void TempFileBudget_ReportsNoNegativeMetrics()
        {
            var metrics = UnityTempFileManager.Shared.GetMetrics();

            Assert.GreaterOrEqual(metrics.ActiveFiles, 0);
            Assert.GreaterOrEqual(metrics.PendingDeleteQueueDepth, 0);
            Assert.GreaterOrEqual(metrics.CleanupRetries, 0);
            Assert.GreaterOrEqual(metrics.CleanupFailures, 0);
            ReliabilityBudgets.Current.Record("temp_files.cleanup_failures", metrics.CleanupFailures, "files");
        }
    }
}
