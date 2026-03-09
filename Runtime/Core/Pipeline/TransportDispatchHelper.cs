using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Stable public helpers for buffered response collection around dispatch-based transports.
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
    }
}
