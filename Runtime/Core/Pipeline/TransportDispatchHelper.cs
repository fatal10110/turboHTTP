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
        /// <summary>Runs <paramref name="dispatch"/> for <paramref name="request"/> and collects the response into a buffered <see cref="UHttpResponse"/>.</summary>
        /// <param name="dispatch">The dispatch delegate to invoke.</param>
        /// <param name="request">The request to send.</param>
        /// <param name="context">The request context.</param>
        /// <param name="cancellationToken">Cancels the dispatch.</param>
        /// <returns>The buffered response.</returns>
        public static Task<UHttpResponse> CollectResponseAsync(
            DispatchFunc dispatch,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return BufferedDispatchBridge.CollectResponseAsync(dispatch, request, context, cancellationToken);
        }

        /// <summary>Sends <paramref name="request"/> through <paramref name="transport"/> and collects the response into a buffered <see cref="UHttpResponse"/>.</summary>
        /// <param name="transport">The transport to use.</param>
        /// <param name="request">The request to send.</param>
        /// <param name="context">The request context.</param>
        /// <param name="cancellationToken">Cancels the dispatch.</param>
        /// <returns>The buffered response.</returns>
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
