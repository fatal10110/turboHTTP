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
        /// <param name="timeout">How long to wait (wall-clock), from zero to <see cref="int.MaxValue"/>
        /// milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
        /// <returns>The response, which the caller owns and disposes.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is out of range. Thrown
        /// before <paramref name="requestTask"/> is consumed, so the caller still owns it.</exception>
        /// <exception cref="TimeoutException">The request did not complete in time. A response that
        /// completes later is disposed by this wrapper; the request itself is not cancelled.</exception>
        [Obsolete(AbandonsRequestMessage)]
        public static UniTask<UHttpResponse> WithTimeout(
            this UniTask<UHttpResponse> requestTask,
            TimeSpan timeout)
        {
            // Validated here, before RaceAsync consumes requestTask: CancelAfter rejects more than
            // int.MaxValue ms, and throwing there would strand the late response.
            if (timeout != Timeout.InfiniteTimeSpan &&
                (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue))
            {
                throw new ArgumentOutOfRangeException(nameof(timeout),
                    "Timeout must be between 0 and int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
            }
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

                // A source that has completed by now goes to the caller; otherwise DisposeLateAsync
                // becomes the single owner of whatever it produces later.
                if (!source.IsCompleted)
                {
                    _ = DisposeLateAsync(source);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new TimeoutException(
                        "The request did not complete within " + timeout + ". It was not cancelled; " +
                        "a late response is disposed.");
                }
            }

            return await source;
        }

        // Owns a response that arrives after the caller stopped waiting: disposes it, and observes a
        // late failure (the caller already got its own exception). The returned task never faults.
        private static async Task DisposeLateAsync(Task<UHttpResponse> source)
        {
            try
            {
                var late = await source.ConfigureAwait(false);
                late?.Dispose();
            }
            catch (Exception)
            {
                // Observed on purpose; nothing is waiting for this outcome.
            }
        }
    }
}
#endif
