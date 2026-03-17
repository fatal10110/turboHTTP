using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    internal sealed class CookieHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CookieJar _jar;
        private readonly Uri _uri;

        internal CookieHandler(IHttpHandler inner, CookieJar jar, Uri uri)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _jar = jar ?? throw new ArgumentNullException(nameof(jar));
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            var setCookieValues = headers.GetValues("Set-Cookie");
            if (setCookieValues.Count > 0)
            {
                _jar.StoreFromSetCookieHeaders(_uri, setCookieValues);
                context.RecordEvent("CookieStored", new Dictionary<string, object>
                {
                    { "count", setCookieValues.Count }
                });
            }

            return _inner.OnResponseStartAsync(statusCode, headers, body, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
        }
    }
}
