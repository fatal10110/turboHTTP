using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile.iOS
{
    public sealed class IosBackgroundTaskBridge : IBackgroundExecutionBridge
    {
        private readonly TimeSpan _fallbackBudget;

        public IosBackgroundTaskBridge(TimeSpan? fallbackBudget = null)
        {
            _fallbackBudget = fallbackBudget ?? TimeSpan.FromSeconds(25);
        }

        public async ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken)
        {
#if !UNITY_IOS || UNITY_EDITOR
            return null;
#else
            var startedAt = DateTime.UtcNow;
            int taskId = await TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(
                () => IosBackgroundTaskBindings.BeginBackgroundTask("TurboHTTP.Request"),
                cancellationToken).ConfigureAwait(false);

            if (taskId < 0)
                return null;

            var expirationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var monitorTask = MonitorExpirationAsync(expirationCts, startedAt, cancellationToken);

            return new Mobile.BackgroundExecutionScope(
                scopeId: "ios-" + taskId,
                startedAtUtc: startedAt,
                expirationToken: expirationCts.Token,
                remainingBudgetProvider: () =>
                {
                    var remaining = IosBackgroundTaskBindings.GetRemainingTime();
                    if (remaining > TimeSpan.Zero)
                        return remaining;

                    var elapsed = DateTime.UtcNow - startedAt;
                    var fallback = _fallbackBudget - elapsed;
                    return fallback > TimeSpan.Zero ? fallback : TimeSpan.Zero;
                },
                disposeAction: async () =>
                {
                    expirationCts.Cancel();
                    expirationCts.Dispose();
                    await TurboHTTP.Unity.MainThreadDispatcher.ExecuteAsync(
                        () => IosBackgroundTaskBindings.EndBackgroundTask(taskId),
                        CancellationToken.None).ConfigureAwait(false);
                    _ = monitorTask;
                });
#endif
        }

#if UNITY_IOS && !UNITY_EDITOR
        private async Task MonitorExpirationAsync(
            CancellationTokenSource expirationCts,
            DateTime startedAt,
            CancellationToken cancellationToken)
        {
            while (!expirationCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var remaining = IosBackgroundTaskBindings.GetRemainingTime();
                if (remaining > TimeSpan.Zero)
                {
                    if (remaining <= TimeSpan.FromSeconds(1))
                    {
                        expirationCts.Cancel();
                        break;
                    }
                }
                else
                {
                    var elapsed = DateTime.UtcNow - startedAt;
                    if (elapsed >= _fallbackBudget)
                    {
                        expirationCts.Cancel();
                        break;
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    break;
                }
            }
        }
#endif
    }
}
