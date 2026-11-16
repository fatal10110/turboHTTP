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
        /// <summary>Dispatches <paramref name="request"/> and buffers the whole response; the caller disposes the returned response.</summary>
        public static Task<UHttpResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return BufferedDispatchBridge.CollectResponseAsync(dispatch, request, context, cancellationToken);
        }

        /// <summary>Dispatches <paramref name="request"/> and buffers the whole response; the caller disposes the returned response.</summary>
        public static Task<UHttpResponse> CollectResponseAsync(
            IHttpTransport transport,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return BufferedDispatchBridge.CollectResponseAsync(transport, request, context, cancellationToken);
        }
    }
}
