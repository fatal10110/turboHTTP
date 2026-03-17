using System;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Transport
{
    internal sealed class NullHandler : IHttpHandler
    {
        internal static readonly NullHandler Instance = new NullHandler();

        private NullHandler()
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
            if (body == null)
                return default;

            return DisposeBodyAsync(body);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
        }

        private static async ValueTask DisposeBodyAsync(IResponseBodySource body)
        {
            body.Abort();
            await body.DisposeAsync().ConfigureAwait(false);
        }
    }
}
