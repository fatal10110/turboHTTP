using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    internal static class ResponseBodyDiscardHelper
    {
        internal static async ValueTask DiscardAsync(
            IResponseBodySource body,
            CancellationToken dispatchCancellationToken,
            TimeSpan responseDiscardTimeout)
        {
            if (body == null)
                return;
            if (responseDiscardTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(responseDiscardTimeout));

            var drained = false;
            var aborted = false;
            CancellationTokenSource discardTimeoutCts = null;
            try
            {
                discardTimeoutCts = dispatchCancellationToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(dispatchCancellationToken)
                    : new CancellationTokenSource();
                discardTimeoutCts.CancelAfter(responseDiscardTimeout);

                try
                {
                    await body.DrainAsync(discardTimeoutCts.Token).ConfigureAwait(false);
                    drained = true;
                }
                catch (Exception ex)
                {
                    // One clause: under IL2CPP a rethrow from an earlier clause of a filtered try is
                    // caught by a later one, which here swallowed the caller's cancellation (#320).
                    body.Abort();
                    aborted = true;
                    if (ex is OperationCanceledException && dispatchCancellationToken.IsCancellationRequested)
                        throw;
                }
            }
            finally
            {
                discardTimeoutCts?.Dispose();

                try
                {
                    if (!drained && !aborted)
                    {
                        body.Abort();
                        aborted = true;
                    }

                    await body.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    if (!aborted)
                    {
                        try
                        {
                            body.Abort();
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
    }
}
