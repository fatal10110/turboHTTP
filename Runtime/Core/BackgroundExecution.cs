using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public interface IBackgroundExecutionScope : IAsyncDisposable
    {
        string ScopeId { get; }
        DateTime StartedAtUtc { get; }
        TimeSpan RemainingBudget { get; }
        CancellationToken ExpirationToken { get; }
    }

    public interface IBackgroundExecutionBridge
    {
        ValueTask<IBackgroundExecutionScope> AcquireAsync(
            RequestContext context,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Optional extension for bridges that can persist deferred background work
    /// outside the current process (e.g., Android WorkManager).
    /// </summary>
    public interface IDeferredBackgroundWorkBridge
    {
        bool TryEnqueueDeferredWork(string dedupeKey);
        bool TryMarkReplayComplete(string dedupeKey);
    }
}
