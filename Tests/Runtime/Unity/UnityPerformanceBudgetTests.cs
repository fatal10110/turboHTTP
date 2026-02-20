using NUnit.Framework;
using TurboHTTP.Unity;

namespace TurboHTTP.Tests.UnityModule
{
    public class UnityPerformanceBudgetTests
    {
        [Test]
        public void DispatcherBudget_GuardsQueueDepthAgainstConfiguredCap()
        {
            var settings = MainThreadDispatcher.GetSettings();
            var metrics = MainThreadDispatcher.GetMetrics();

            Assert.LessOrEqual(
                metrics.Queue.UserQueueDepth,
                settings.UserQueueCapacity,
                "Dispatcher queue depth exceeded configured cap.");
        }

        [Test]
        public void TempFileBudget_ReportsNoNegativeMetrics()
        {
            var metrics = UnityTempFileManager.Shared.GetMetrics();

            Assert.GreaterOrEqual(metrics.ActiveFiles, 0);
            Assert.GreaterOrEqual(metrics.PendingDeleteQueueDepth, 0);
            Assert.GreaterOrEqual(metrics.CleanupRetries, 0);
            Assert.GreaterOrEqual(metrics.CleanupFailures, 0);
        }
    }
}
