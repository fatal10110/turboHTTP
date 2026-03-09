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

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
        }

        public void OnResponseData(System.ReadOnlySpan<byte> chunk, RequestContext context)
        {
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
        }
    }
}
