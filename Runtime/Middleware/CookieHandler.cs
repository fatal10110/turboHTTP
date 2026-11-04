using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Stores Set-Cookie from the response against the request-uri of the hop that produced it
    /// (RFC 6265 section 5.3). The uri follows <see cref="OnRequestStart"/>, which a wrapping
    /// <see cref="RedirectHandler"/> forwards once per hop.
    /// </summary>
    internal sealed class CookieHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;
        private readonly CookieJar _jar;
        private Uri _uri;

        internal CookieHandler(IHttpHandler inner, CookieJar jar, Uri uri)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _jar = jar ?? throw new ArgumentNullException(nameof(jar));
            _uri = uri ?? throw new ArgumentNullException(nameof(uri));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            // Callbacks of one request are sequential (one hop at a time), so a plain field is enough.
            if (request?.Uri != null && request.Uri.IsAbsoluteUri)
                _uri = request.Uri;

            _inner.OnRequestStart(request, context);
        }

        public ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            StoreSetCookies(_jar, _uri, headers, context);
            return _inner.OnResponseStartAsync(statusCode, headers, body, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
        }

        internal static void StoreSetCookies(CookieJar jar, Uri requestUri, HttpHeaders headers, RequestContext context)
        {
            if (headers == null)
                return;

            var setCookieValues = headers.GetValues("Set-Cookie");
            if (setCookieValues.Count == 0)
                return;

            jar.StoreFromSetCookieHeaders(requestUri, setCookieValues);
            context?.RecordEvent("CookieStored", new Dictionary<string, object>
            {
                { "count", setCookieValues.Count }
            });
        }
    }
}
