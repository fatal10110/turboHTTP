using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Stable public helpers for buffered collection and callback delivery around dispatch-based transports.
    /// Shipping assemblies should depend on this surface rather than <c>DispatchBridge</c>.
    /// </summary>
    public static class TransportDispatchHelper
    {
        public static Task<UHttpResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return DispatchBridge.CollectResponseAsync(dispatch, request, context, cancellationToken);
        }

        public static Task<UHttpResponse> CollectResponseAsync(
            IHttpTransport transport,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return DispatchBridge.CollectResponseAsync(transport, request, context, cancellationToken);
        }

        public static void DeliverResponse(
            UHttpResponse response,
            IHttpHandler handler,
            RequestContext context,
            UHttpRequest fallbackRequest)
        {
            DispatchBridge.DeliverResponse(response, handler, context, fallbackRequest);
        }

        /// <summary>
        /// Records the buffered caller-visible cancellation exception that should be surfaced
        /// after the handler chain has observed <see cref="IHttpHandler.OnResponseError"/>.
        /// </summary>
        public static void SetCancellationException(
            RequestContext context,
            OperationCanceledException exception)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (exception == null)
                throw new ArgumentNullException(nameof(exception));

            context.SetCancellationException(exception);
        }
    }
}
