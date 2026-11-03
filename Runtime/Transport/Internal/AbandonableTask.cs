using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// Awaits an operation that does not honor a <see cref="CancellationToken"/> itself (socket
    /// connect, the token-less <c>SslStream.AuthenticateAsClientAsync</c> overload) and abandons it
    /// when the token fires. An abandoned task still runs until the caller disposes the resource
    /// it works on, and then usually faults; <see cref="WaitAsync"/> attaches an observer before
    /// returning so that fault never surfaces as <see cref="TaskScheduler.UnobservedTaskException"/>
    /// (#172).
    /// </summary>
    internal static class AbandonableTask
    {
        /// <summary>
        /// Completes with <paramref name="task"/>, or throws <see cref="System.OperationCanceledException"/>
        /// as soon as <paramref name="ct"/> is cancelled while the task is still running. On
        /// cancellation the task is observed (<see cref="Observe"/>) and left running: the caller
        /// owns the resource the task works on and disposes it in its own failure path, which ends
        /// the task. The cancellation registration is released on every path.
        /// </summary>
        internal static async Task WaitAsync(Task task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return;
            }

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelSignal))
            {
                var completed = await Task.WhenAny(task, cancelSignal.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, task))
                {
                    Observe(task);
                    throw new System.OperationCanceledException(ct);
                }
            }

            await task.ConfigureAwait(false);
        }

        /// <summary>
        /// Marks a later fault of <paramref name="task"/> as observed. The continuation keeps the
        /// task reachable until it has read <see cref="Task.Exception"/>, so the finalizer never
        /// reports it.
        /// </summary>
        internal static void Observe(Task task)
        {
            _ = task.ContinueWith(
                static t => { _ = t.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
