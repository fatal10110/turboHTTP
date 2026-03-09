using System;
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

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
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
