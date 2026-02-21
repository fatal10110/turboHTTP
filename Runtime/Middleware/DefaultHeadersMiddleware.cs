using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Middleware that adds default headers to all requests.
    /// Headers are only added if they don't already exist on the request
    /// (unless overrideExisting is true).
    /// </summary>
    public class DefaultHeadersMiddleware : IHttpMiddleware
    {
        private readonly HttpHeaders _defaultHeaders;
        private readonly bool _overrideExisting;

        public DefaultHeadersMiddleware(HttpHeaders defaultHeaders, bool overrideExisting = false)
        {
            _defaultHeaders = defaultHeaders ?? new HttpHeaders();
            _overrideExisting = overrideExisting;
        }

        public async ValueTask<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            if (_defaultHeaders.Count == 0)
                return await next(request, context, cancellationToken);

            // Clone headers to avoid modifying original request
            var headers = request.Headers.Clone();

            // Add default headers (preserving multi-value headers)
            foreach (var name in _defaultHeaders.Names)
            {
                if (_overrideExisting || !headers.Contains(name))
                {
                    var values = _defaultHeaders.GetValues(name);
                    if (values != null)
                    {
                        headers.Set(name, values[0]);
                        for (int i = 1; i < values.Count; i++)
                            headers.Add(name, values[i]);
                    }
                }
            }

            // Create new request with updated headers
            var modifiedRequest = request.WithHeaders(headers);
            context.UpdateRequest(modifiedRequest);

            // Continue pipeline
            return await next(modifiedRequest, context, cancellationToken);
        }
    }
}
