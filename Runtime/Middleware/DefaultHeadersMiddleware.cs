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

            // Add default headers (preserving multi-value headers)
            foreach (var name in _defaultHeaders.Names)
            {
                if (_overrideExisting || !request.Headers.Contains(name))
                {
                    var values = _defaultHeaders.GetValues(name);
                    if (values != null)
                    {
                        request.Headers.Set(name, values[0]);
                        for (int i = 1; i < values.Count; i++)
                            request.Headers.Add(name, values[i]);
                    }
                }
            }

            context.UpdateRequest(request);

            // Continue pipeline
            return await next(request, context, cancellationToken);
        }
    }
}
