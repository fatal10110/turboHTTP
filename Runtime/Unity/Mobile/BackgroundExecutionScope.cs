using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile
{
    internal sealed class BackgroundExecutionScope : IBackgroundExecutionScope
    {
        private readonly Func<TimeSpan> _remainingBudgetProvider;
        private readonly Func<ValueTask> _disposeAction;
        private int _disposed;

        public BackgroundExecutionScope(
            string scopeId,
            DateTime startedAtUtc,
            CancellationToken expirationToken,
            Func<TimeSpan> remainingBudgetProvider,
            Func<ValueTask> disposeAction)
        {
            ScopeId = scopeId ?? Guid.NewGuid().ToString("N");
            StartedAtUtc = startedAtUtc;
            ExpirationToken = expirationToken;
            _remainingBudgetProvider = remainingBudgetProvider ?? (() => TimeSpan.Zero);
            _disposeAction = disposeAction ?? (() => default);
        }

        public string ScopeId { get; }

        public DateTime StartedAtUtc { get; }

        public TimeSpan RemainingBudget => _remainingBudgetProvider();

        public CancellationToken ExpirationToken { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            await _disposeAction().ConfigureAwait(false);
        }
    }
}
