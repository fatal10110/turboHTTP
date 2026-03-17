using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// A transitional utility class that bridges the execution of a <see cref="DispatchFunc"/> 
    /// and collects its result into a <see cref="UHttpResponse"/>.
    /// </summary>
    internal static class DispatchBridge
    {
        internal static Task<UHttpResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            return BufferedDispatchBridge.CollectResponseAsync(
                dispatch,
                request,
                context,
                cancellationToken);
        }

        internal static Task<UHttpResponse> CollectResponseAsync(
            IHttpTransport transport,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken)
        {
            if (transport == null)
                throw new ArgumentNullException(nameof(transport));

            return BufferedDispatchBridge.CollectResponseAsync(
                transport,
                request,
                context,
                cancellationToken);
        }

        internal static void AttachCompletion(Task dispatchTask, BufferedResponseCollectorHandler collector)
        {
            BufferedDispatchBridge.AttachCompletion(dispatchTask, collector);
        }
    }
}
