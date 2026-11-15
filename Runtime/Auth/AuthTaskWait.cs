using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Awaits a task that this caller does not own (a shared refresh, a token provider call) with
    /// the caller's own cancellation: cancelling stops the wait, not the task.
    /// </summary>
    internal static class AuthTaskWait
    {
        internal static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken cancellationToken)
        {
            if (task == null)
                throw new ArgumentNullException(nameof(task));

            if (task.IsCompleted || !cancellationToken.CanBeCanceled)
                return await task.ConfigureAwait(false);

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(
                       state => ((TaskCompletionSource<bool>)state).TrySetResult(true),
                       cancelled))
            {
                var winner = await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false);
                if (!ReferenceEquals(winner, task))
                {
                    ObserveFault(task);
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            return await task.ConfigureAwait(false);
        }

        /// <summary>Marks a fault of an abandoned task observed, whenever it happens.</summary>
        internal static void ObserveFault(Task task)
        {
            task.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
