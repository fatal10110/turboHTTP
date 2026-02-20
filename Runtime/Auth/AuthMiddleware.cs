using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Middleware that adds authentication headers to requests.
    /// Supports Bearer tokens (default), Basic auth, and custom schemes.
    /// </summary>
    public class AuthMiddleware : IHttpMiddleware
    {
        private readonly IAuthTokenProvider _tokenProvider;
        private readonly string _scheme;

        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="scheme">Auth scheme (e.g., "Bearer", "Basic"). Default: "Bearer"</param>
        public AuthMiddleware(IAuthTokenProvider tokenProvider, string scheme = "Bearer")
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            if (scheme != null && (scheme.Contains('\r') || scheme.Contains('\n')))
                throw new ArgumentException("Auth scheme must not contain CR or LF characters", nameof(scheme));
            _scheme = scheme;
        }

        public async Task<UHttpResponse> InvokeAsync(
            UHttpRequest request,
            RequestContext context,
            HttpPipelineDelegate next,
            CancellationToken cancellationToken)
        {
            var token = await _tokenProvider.GetTokenAsync(cancellationToken);

            if (!string.IsNullOrEmpty(token))
            {
                if (token.Contains('\r') || token.Contains('\n'))
                    throw new ArgumentException("Auth token must not contain CR or LF characters");

                var headers = request.Headers.Clone();
                headers.Set("Authorization", $"{_scheme} {token}");

                var modifiedRequest = request.WithHeaders(headers);
                context.UpdateRequest(modifiedRequest);

                return await next(modifiedRequest, context, cancellationToken);
            }

            return await next(request, context, cancellationToken);
        }
    }
}
