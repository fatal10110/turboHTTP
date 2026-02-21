#if TURBOHTTP_UNITASK
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.UniTask
{
    /// <summary>
    /// Thin wrappers over UniTask-native cancellation and timeout operators.
    /// </summary>
    public static class UniTaskCancellationExtensions
    {
        public static UniTask<UHttpResponse> WithTimeout(
            this UniTask<UHttpResponse> requestTask,
            TimeSpan timeout)
        {
            return requestTask.Timeout(timeout);
        }

        public static UniTask<UHttpResponse> AttachToCancellationToken(
            this UniTask<UHttpResponse> requestTask,
            CancellationToken cancellationToken)
        {
            return requestTask.AttachExternalCancellation(cancellationToken);
        }
    }
}
#endif
