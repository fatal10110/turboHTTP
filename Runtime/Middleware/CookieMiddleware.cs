using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Adds outbound Cookie headers and persists inbound Set-Cookie headers.
    /// </summary>
    public sealed class CookieMiddleware : IHttpMiddleware
    {
        private readonly CookieJar _jar;

        public CookieJar Jar => _jar;

        public CookieMiddleware(CookieJar jar = null)
        {
            _jar = jar ?? new CookieJar();
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            var effectiveRequest = request;

            if (request.Uri.IsAbsoluteUri
                && (string.Equals(request.Uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(request.Uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)))
            {
                bool isCrossSite = false;
                if (request.Metadata != null
                    && request.Metadata.TryGetValue(RequestMetadataKeys.IsCrossSiteRequest, out var crossSiteRaw)
                    && crossSiteRaw is bool crossSiteBool)
                {
                    isCrossSite = crossSiteBool;
                }

                var jarCookieHeader = _jar.GetCookieHeader(request.Uri, request.Method, isCrossSite);
                if (!string.IsNullOrEmpty(jarCookieHeader))
                {
                    var headers = request.Headers.Clone();
                    var existingCookieHeader = headers.Get("Cookie");
                    if (string.IsNullOrEmpty(existingCookieHeader))
                        headers.Set("Cookie", jarCookieHeader);
                    else
                        headers.Set("Cookie", existingCookieHeader + "; " + jarCookieHeader);

                    effectiveRequest = new UHttpRequest(
                        request.Method,
                        request.Uri,
                        headers,
                        request.Body,
                        request.Timeout,
                        request.Metadata);

                    context.UpdateRequest(effectiveRequest);
                    context.RecordEvent("CookieAttached");
                }
            }

            var response = await next(effectiveRequest, context, cancellationToken).ConfigureAwait(false);

            var setCookieValues = response.Headers.GetValues("Set-Cookie");
            if (setCookieValues.Count > 0)
            {
                _jar.StoreFromSetCookieHeaders(effectiveRequest.Uri, setCookieValues);
                context.RecordEvent("CookieStored", new Dictionary<string, object>
                {
                    { "count", setCookieValues.Count }
                });
            }

            return response;
        }
    }
}
