using System;
using System.Threading;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// Recognizes the caller's cancellation inside a failure that reached a bridge as an error: an
    /// <see cref="OperationCanceledException"/> (any subtype, <see cref="System.Threading.Tasks.TaskCanceledException"/>
    /// included) wrapped in <see cref="UHttpException"/>, <see cref="AggregateException"/> or a handler fault,
    /// while the caller's token has fired. The error model (cancellation is an
    /// <see cref="OperationCanceledException"/>) then holds even when a lower layer wrapped the cancellation,
    /// as IL2CPP builds did (#320). Without the caller's token the failure keeps its type: a transport
    /// deadline stays <see cref="UHttpErrorType.Timeout"/>, a connection shutdown stays a network error.
    /// </summary>
    internal static class CancellationClassification
    {
        // Wrappers are shallow (a transport error around a task cancellation); the bound only stops a
        // pathological or cyclic InnerException chain.
        private const int MaxUnwrapDepth = 8;

        /// <summary>
        /// True when <paramref name="error"/> wraps an <see cref="OperationCanceledException"/> and
        /// <paramref name="callerToken"/> has fired; <paramref name="canceled"/> is then a cancellation
        /// carrying the caller's token, with <paramref name="error"/> as its inner exception.
        /// </summary>
        internal static bool TryGetCallerCancellation(
            Exception error,
            CancellationToken callerToken,
            out OperationCanceledException canceled)
        {
            canceled = null;
            if (error == null || !callerToken.IsCancellationRequested)
                return false;

            var current = error;
            for (int depth = 0; current != null && depth < MaxUnwrapDepth; depth++)
            {
                if (current is OperationCanceledException found)
                {
                    canceled = ReferenceEquals(found, error) && found.CancellationToken == callerToken
                        ? found
                        : new OperationCanceledException(found.Message, error, callerToken);
                    return true;
                }

                if (current is AggregateException aggregate)
                {
                    if (aggregate.InnerExceptions.Count != 1)
                        return false;

                    current = aggregate.InnerExceptions[0];
                }
                else if (current is UHttpException || current is HandlerCallbackException)
                {
                    current = current.InnerException;
                }
                else
                {
                    return false;
                }
            }

            return false;
        }
    }
}
