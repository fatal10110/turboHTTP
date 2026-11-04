using System;

namespace TurboHTTP.Unity
{
    /// <summary>
    /// Why the main-thread user queue did not run a work item.
    /// </summary>
    public enum MainThreadDispatchRejectionReason
    {
        /// <summary>
        /// The user queue was full under <see cref="MainThreadBackpressurePolicy.Reject"/>, or full
        /// for an immediate enqueue that cannot wait.
        /// </summary>
        QueueFull = 0,

        /// <summary>
        /// The user queue was full under <see cref="MainThreadBackpressurePolicy.Wait"/> and
        /// <see cref="MainThreadDispatcherSettings.MaxPendingWaiters"/> callers were already waiting.
        /// </summary>
        WaiterLimitReached = 1,

        /// <summary>
        /// The user queue stayed full for <see cref="MainThreadDispatcherSettings.WaitTimeout"/>
        /// under <see cref="MainThreadBackpressurePolicy.Wait"/>.
        /// </summary>
        WaitTimedOut = 2,

        /// <summary>
        /// The queued item was evicted to make room for newer work
        /// (<see cref="MainThreadBackpressurePolicy.DropOldest"/>, or a capacity reduction).
        /// </summary>
        DroppedOldest = 3
    }

    /// <summary>
    /// Thrown, or set on the task returned by <see cref="MainThreadDispatcher.ExecuteAsync(Action, System.Threading.CancellationToken)"/>
    /// and its overloads, when user-plane backpressure kept the work from running. The work item never
    /// started. Control-plane work (<see cref="MainThreadDispatcher.ExecuteControlAsync(Action, System.Threading.CancellationToken)"/>)
    /// is never rejected for backpressure.
    /// </summary>
    /// <remarks>
    /// Derives from <see cref="InvalidOperationException"/> so callers that already catch the queue-full
    /// rejection keep working. Work evicted under <see cref="MainThreadBackpressurePolicy.DropOldest"/>
    /// faults with this exception instead of being reported as canceled, so a drop can be told apart
    /// from the caller's own cancellation.
    /// </remarks>
    public sealed class MainThreadDispatchRejectedException : InvalidOperationException
    {
        /// <summary>
        /// Creates the exception.
        /// </summary>
        /// <param name="reason">Why the work did not run.</param>
        /// <param name="message">Diagnostic message.</param>
        /// <param name="innerException">Underlying cause, for example the wait timeout.</param>
        public MainThreadDispatchRejectedException(
            MainThreadDispatchRejectionReason reason,
            string message,
            Exception innerException = null)
            : base(message, innerException)
        {
            Reason = reason;
        }

        /// <summary>
        /// Why the work did not run.
        /// </summary>
        public MainThreadDispatchRejectionReason Reason { get; }
    }
}
