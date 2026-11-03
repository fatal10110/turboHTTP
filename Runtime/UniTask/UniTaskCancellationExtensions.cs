#if TURBOHTTP_UNITASK
using System;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Timeout and cancellation wrappers for an already-started <see cref="UniTask{T}"/> of
    /// <see cref="UHttpResponse"/>.
    /// </summary>
    /// <remarks>
    /// A started request cannot be reached from its <see cref="UniTask{T}"/>, so these wrappers only stop
    /// waiting: the request keeps its connection until the transport finishes it. To abort the request
    /// itself, pass the <see cref="CancellationToken"/> to <c>GetAsync</c>/<c>AsUniTask</c>, or set a
    /// deadline with <see cref="UHttpRequest.WithTimeout(TimeSpan)"/>, which the transport enforces
    /// (<see cref="UHttpErrorType.Timeout"/>). UniTask's own <c>Timeout</c>/<c>AttachExternalCancellation</c>
    /// operators also leave the request running, and drop a response that arrives after they gave up.
    /// </remarks>
    public static class UniTaskCancellationExtensions
    {
        internal const string AbandonsRequestMessage =
            "Stops waiting but cannot cancel a started request. Pass the CancellationToken to GetAsync/AsUniTask, " +
            "or set the deadline with UHttpRequest.WithTimeout(timeout) before sending.";

        /// <summary>
        /// Waits for <paramref name="requestTask"/> for at most <paramref name="timeout"/>.
        /// </summary>
        /// <param name="requestTask">The started request.</param>
        /// <param name="timeout">How long to wait (wall-clock), or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
        /// <returns>The response, which the caller owns and disposes.</returns>
        /// <exception cref="TimeoutException">The request did not complete in time. A response that
        /// completes later is disposed by this wrapper; the request itself is not cancelled.</exception>
        [Obsolete(AbandonsRequestMessage)]
        public static UniTask<UHttpResponse> WithTimeout(
            this UniTask<UHttpResponse> requestTask,
            TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be >= 0 or Timeout.InfiniteTimeSpan.");
            return RaceAsync(requestTask, timeout, CancellationToken.None);
        }

        /// <summary>
        /// Waits for <paramref name="requestTask"/> until <paramref name="cancellationToken"/> is cancelled.
        /// </summary>
        /// <param name="requestTask">The started request.</param>
        /// <param name="cancellationToken">Stops the wait.</param>
        /// <returns>The response, which the caller owns and disposes.</returns>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled
        /// first. A response that completes later is disposed by this wrapper; the request itself is not
        /// cancelled.</exception>
        [Obsolete(AbandonsRequestMessage)]
        public static UniTask<UHttpResponse> AttachToCancellationToken(
            this UniTask<UHttpResponse> requestTask,
            CancellationToken cancellationToken)
        {
            return RaceAsync(requestTask, Timeout.InfiniteTimeSpan, cancellationToken);
        }

        private static async UniTask<UHttpResponse> RaceAsync(
            UniTask<UHttpResponse> requestTask,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var source = requestTask.AsTask();
            if (!source.IsCompleted)
            {
                using (var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using (stopWaiting.Token.Register(
                        static state => ((TaskCompletionSource<bool>)state).TrySetResult(true), stopped))
                    {
                        if (timeout != Timeout.InfiniteTimeSpan)
                            stopWaiting.CancelAfter(timeout);
                        await Task.WhenAny(source, stopped.Task);
                    }
                }

                // A source that has completed by now goes to the caller; otherwise the continuation in
                // DisposeWhenCompleted becomes the single owner of whatever it produces later.
                if (!source.IsCompleted)
                {
                    DisposeWhenCompleted(source);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "The request did not complete within " + timeout + ". It was not cancelled; " +
                        "a late response is disposed.");
                }
            }

            return await source;
        }

        private static void DisposeWhenCompleted(Task<UHttpResponse> source)
        {
            _ = source.ContinueWith(
                static completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        completed.Result?.Dispose();
                    else
                        _ = completed.Exception; // observed: the caller already got its own exception
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
#endif
