using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Interceptor that adds authentication headers to requests.
    /// Supports Bearer tokens (default), Basic auth, and custom schemes.
    /// </summary>
    public sealed class AuthInterceptor : IHttpInterceptor
    {
        private readonly IAuthTokenProvider _tokenProvider;
        private readonly string _scheme;

        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="scheme">Auth scheme (e.g., "Bearer", "Basic"). Default: "Bearer"</param>
        public AuthInterceptor(IAuthTokenProvider tokenProvider, string scheme = "Bearer")
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            if (scheme != null && (scheme.Contains('\r') || scheme.Contains('\n')))
                throw new ArgumentException("Auth scheme must not contain CR or LF characters", nameof(scheme));
            _scheme = scheme;
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                var requestForNext = request;

                if (!string.IsNullOrEmpty(token))
                {
                    if (token.Contains('\r') || token.Contains('\n'))
                        throw new ArgumentException("Auth token must not contain CR or LF characters");

                    requestForNext = request.Clone();
                    requestForNext.WithHeader("Authorization", BuildAuthorizationValue(token));
                    context.UpdateRequest(requestForNext);
                }

                await next(requestForNext, handler, context, cancellationToken).ConfigureAwait(false);
            };
        }

        private string BuildAuthorizationValue(string token)
        {
            if (string.IsNullOrEmpty(_scheme))
                return token;

            return _scheme + " " + token;
        }
    }
}
