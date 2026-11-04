using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Awaits an operation that does not honor a <see cref="CancellationToken"/> itself (socket
    /// connect, the token-less <c>SslStream.AuthenticateAsClientAsync</c> overload) and abandons it
    /// when the token fires. An abandoned task still runs until the caller disposes the resource
    /// it works on, and then usually faults; <see cref="WaitAsync(Task, CancellationToken, IDisposable)"/> attaches an observer before
    /// returning so that fault never surfaces as <see cref="TaskScheduler.UnobservedTaskException"/>
    /// (#172), and records it in <see cref="AbandonedSocketIo"/>, which holds back the next socket until it
    /// ended (#369). It lives in Core so the WebSocket handshake reads (#226/#345), which may not reference
    /// Transport, race their reads the same way; Transport keeps <c>AbandonableTask</c> as a forwarder.
    /// </summary>
    internal static class AbandonOnCancel
    {
        /// <summary>
        /// Completes with <paramref name="task"/>, or throws <see cref="OperationCanceledException"/>
        /// as soon as <paramref name="ct"/> is cancelled while the task is still running. On
        /// cancellation the task is observed (<see cref="Observe(Task)"/>) and left running: the caller
        /// owns the resource the task works on and disposes it in its own failure path, which ends
        /// the task. The cancellation registration is released on every path.
        /// </summary>
        /// <param name="task">The operation to await.</param>
        /// <param name="ct">The token that abandons the operation.</param>
        /// <param name="abandonedResource">
        /// Optional resource the abandoned operation works on (a stream whose read was abandoned):
        /// disposed once the abandoned task ends, because its result was discarded and the resource
        /// can no longer be used in order.
        /// </param>
        internal static async Task WaitAsync(Task task, CancellationToken ct, IDisposable abandonedResource = null)
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
                    Abandon(task, abandonedResource);
                    throw new OperationCanceledException(ct);
                }
            }

            await task.ConfigureAwait(false);
        }

        /// <summary>
        /// <see cref="WaitAsync(Task, CancellationToken, IDisposable)"/> for an operation with a
        /// result. A result that arrives after the operation was abandoned is discarded. Mono and
        /// IL2CPP may never end a pending socket read after its token fired and its socket was
        /// disposed (#293), so callers on a read path use this instead of trusting the platform.
        /// </summary>
        internal static async Task<T> WaitAsync<T>(Task<T> task, CancellationToken ct, IDisposable abandonedResource = null)
        {
            if (!ct.CanBeCanceled || task.IsCompleted)
                return await task.ConfigureAwait(false);

            var cancelSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), cancelSignal))
            {
                var completed = await Task.WhenAny(task, cancelSignal.Task).ConfigureAwait(false);
                if (!ReferenceEquals(completed, task))
                {
                    Abandon(task, abandonedResource);
                    throw new OperationCanceledException(ct);
                }
            }

            return await task.ConfigureAwait(false);
        }

        /// <summary>
        /// The cancel-wins branch of both <c>WaitAsync</c> overloads: the still-running operation
        /// (a socket read, handshake or connect) is observed, its resource disposed once it ends, and
        /// it is recorded in <see cref="AbandonedSocketIo"/> until it ends, so no new socket is created
        /// while IL2CPP's I/O selector may still hold its job under a descriptor number the new socket
        /// could reuse (#369).
        /// </summary>
        private static void Abandon(Task task, IDisposable abandonedResource)
        {
            AbandonedSocketIo.Track(task);
            Observe(task, abandonedResource);
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

        /// <summary>
        /// <see cref="Observe(Task)"/>, and disposes <paramref name="abandonedResource"/> (best
        /// effort) once <paramref name="task"/> ends in any state. A task that never ends keeps the
        /// continuation and the resource until both are collected.
        /// </summary>
        internal static void Observe(Task task, IDisposable abandonedResource)
        {
            if (abandonedResource == null)
            {
                Observe(task);
                return;
            }

            _ = task.ContinueWith(
                static (t, state) =>
                {
                    _ = t.Exception;
                    try { ((IDisposable)state).Dispose(); } catch { /* best effort */ }
                },
                abandonedResource,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
