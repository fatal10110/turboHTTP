using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http2;

namespace TurboHTTP.Tests.Transport.Http2.Helpers
{
    internal static class Http2ConnectionTestExtensions
    {
        public static ValueTask<UHttpResponse> SendRequestAsync(
            this Http2Connection connection,
            UHttpRequest request,
            RequestContext context,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<UHttpResponse>(
                TransportDispatchHelper.CollectResponseAsync(
                    connection.DispatchAsync,
                    request,
                    context,
                    cancellationToken));
        }
    }

    internal sealed class NoOpHttpHandler : IHttpHandler
    {
        internal static readonly NoOpHttpHandler Instance = new NoOpHttpHandler();

        private NoOpHttpHandler()
        {
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
        }

        public ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            body?.Abort();
            return default;
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
        }
    }
}
