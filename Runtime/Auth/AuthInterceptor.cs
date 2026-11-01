using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Interceptor that adds authentication headers to requests.
    /// Supports Bearer tokens (default), Basic auth, and custom schemes.
    /// </summary>
    /// <remarks>
    /// The credential is bound to the origin (scheme, host, port) of the first request this
    /// interceptor sees for a given <see cref="RequestContext"/>. When the interceptor runs inside a
    /// <c>RedirectInterceptor</c> and a redirect hop targets a different origin, no
    /// <c>Authorization</c> header is attached to that hop (RFC 9110 section 15.4), so the
    /// interceptor order does not decide whether a token leaks. Redirects back to the original
    /// origin carry the credential again.
    /// </remarks>
    public sealed class AuthInterceptor : IHttpInterceptor, IInterceptorRole
    {
        // Pipeline ordering role, checked by UHttpClient (#55).
        InterceptorRole IInterceptorRole.Role => InterceptorRole.Auth;

        /// <summary>
        /// Request metadata key (value <c>true</c>) that makes the interceptor pass the request on
        /// without fetching or attaching a credential. Set by <see cref="OAuthClient"/> on its token
        /// and discovery requests, which must not carry the access token and must not re-enter the
        /// token provider (refresh deadlock).
        /// </summary>
        internal const string SkipAuthMetadataKey = "turbohttp.auth.skip";

        private static int s_nextInstanceId;

        private readonly IAuthTokenProvider _tokenProvider;
        private readonly string _scheme;
        private readonly string _originStateKey;

        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="scheme">Auth scheme (e.g., "Bearer", "Basic"). Default: "Bearer"</param>
        public AuthInterceptor(IAuthTokenProvider tokenProvider, string scheme = "Bearer")
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            if (scheme != null && (scheme.Contains('\r') || scheme.Contains('\n')))
                throw new ArgumentException("Auth scheme must not contain CR or LF characters", nameof(scheme));
            _scheme = scheme;
            _originStateKey = "TurboHTTP.Auth.AuthInterceptor.Origin." +
                              Interlocked.Increment(ref s_nextInstanceId).ToString(CultureInfo.InvariantCulture);
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                if (IsSkipRequested(request) || !IsBoundOrigin(request, context))
                {
                    await next(request, handler, context, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var token = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
                var requestForNext = request;

                if (!string.IsNullOrEmpty(token))
                {
                    if (token.Contains('\r') || token.Contains('\n'))
                        throw new ArgumentException("Auth token must not contain CR or LF characters");

                    requestForNext = request.CopyWithSharedContent();
                    requestForNext.WithHeader("Authorization", BuildAuthorizationValue(token));
                    context.UpdateRequest(requestForNext);
                }

                Task dispatchTask;
                try
                {
                    dispatchTask = next(requestForNext, handler, context, cancellationToken);
                }
                catch (Exception)
                {
                    if (!ReferenceEquals(requestForNext, request))
                    {
                        context.UpdateRequest(request);
                        requestForNext.Dispose();
                    }

                    throw;
                }

                await dispatchTask.ConfigureAwait(false);
            };
        }

        private static bool IsSkipRequested(UHttpRequest request)
        {
            var metadata = request.Metadata;
            return metadata != null &&
                   metadata.TryGetValue(SkipAuthMetadataKey, out var raw) &&
                   raw is bool skip &&
                   skip;
        }

        /// <summary>
        /// Records the origin of the first request seen for <paramref name="context"/> and reports
        /// whether <paramref name="request"/> targets that origin (scheme, host, port). Hops of one
        /// request run sequentially, so the context state is never raced for one key.
        /// </summary>
        private bool IsBoundOrigin(UHttpRequest request, RequestContext context)
        {
            var uri = request.Uri;
            if (context == null || uri == null || !uri.IsAbsoluteUri)
                return true;

            var bound = context.GetState<Uri>(_originStateKey);
            if (bound == null)
            {
                context.SetState(_originStateKey, uri);
                return true;
            }

            return string.Equals(bound.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(bound.Host, uri.Host, StringComparison.OrdinalIgnoreCase) &&
                   bound.Port == uri.Port;
        }

        private string BuildAuthorizationValue(string token)
        {
            if (string.IsNullOrEmpty(_scheme))
                return token;

            return _scheme + " " + token;
        }
    }
}
