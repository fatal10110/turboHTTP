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
    /// <para>
    /// Target scope: with <see cref="AuthInterceptorOptions.AllowedOrigins"/> set, the token is
    /// attached only to requests whose origin (scheme, host, port) is in that list (RFC 6750
    /// section 5.3, RFC 9110 section 11.6.2). Without it the token is attached for every origin the
    /// client is used with, so use a dedicated client per authenticated origin or set the list.
    /// </para>
    /// <para>
    /// A request that already carries an <c>Authorization</c> header (for example one set with
    /// <see cref="AuthBuilderExtensions.WithBearerToken"/>) is sent unchanged unless
    /// <see cref="AuthInterceptorOptions.OverrideExistingAuthorization"/> is set.
    /// </para>
    /// <para>
    /// Redirect guard: the credential is also bound to the origin of the first request this
    /// interceptor sees for a given <see cref="RequestContext"/>. When the interceptor runs inside a
    /// <c>RedirectInterceptor</c> and a redirect hop targets a different origin, no
    /// <c>Authorization</c> header is attached to that hop (RFC 9110 section 15.4), even when the
    /// other origin is allowed, so the interceptor order does not decide whether a token leaks.
    /// Redirects back to the original origin carry the credential again.
    /// </para>
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

        // Immutable snapshot of AuthInterceptorOptions.AllowedOrigins; empty = every origin.
        private readonly Uri[] _allowedOrigins;
        private readonly bool _overrideExistingAuthorization;

        /// <summary>
        /// Creates an interceptor that attaches the token to requests for every origin and leaves a
        /// request that already carries an <c>Authorization</c> header unchanged. Use
        /// <see cref="AuthInterceptor(IAuthTokenProvider, AuthInterceptorOptions)"/> to restrict the
        /// origins.
        /// </summary>
        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="scheme">Auth scheme (e.g., "Bearer", "Basic"). Default: "Bearer"</param>
        public AuthInterceptor(IAuthTokenProvider tokenProvider, string scheme = "Bearer")
            : this(tokenProvider, scheme, Array.Empty<Uri>(), overrideExistingAuthorization: false)
        {
        }

        /// <summary>
        /// Creates an interceptor configured by <paramref name="options"/>: the scheme, the origins
        /// the token may be sent to, and whether an existing <c>Authorization</c> header is replaced.
        /// The options are copied; later changes to them have no effect on this interceptor.
        /// </summary>
        /// <param name="tokenProvider">Provider that supplies auth tokens</param>
        /// <param name="options">Scheme, allowed origins and override setting</param>
        /// <exception cref="ArgumentNullException"><paramref name="tokenProvider"/> or
        /// <paramref name="options"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">The scheme contains CR or LF, or an allowed origin is
        /// <c>null</c>, not an absolute <c>http</c>/<c>https</c> URI, or has user info, a path, a
        /// query or a fragment.</exception>
        public AuthInterceptor(IAuthTokenProvider tokenProvider, AuthInterceptorOptions options)
            : this(
                tokenProvider,
                (options ?? throw new ArgumentNullException(nameof(options))).Scheme,
                SnapshotOrigins(options),
                options.OverrideExistingAuthorization)
        {
        }

        private AuthInterceptor(
            IAuthTokenProvider tokenProvider,
            string scheme,
            Uri[] allowedOrigins,
            bool overrideExistingAuthorization)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            if (scheme != null && (scheme.Contains('\r') || scheme.Contains('\n')))
                throw new ArgumentException("Auth scheme must not contain CR or LF characters", nameof(scheme));
            _scheme = scheme;
            _allowedOrigins = allowedOrigins;
            _overrideExistingAuthorization = overrideExistingAuthorization;
            _originStateKey = "TurboHTTP.Auth.AuthInterceptor.Origin." +
                              Interlocked.Increment(ref s_nextInstanceId).ToString(CultureInfo.InvariantCulture);
        }

        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                // Bind the redirect guard first, even for a request that is passed through: a skipped
                // first hop (e.g. caller-set Authorization) must still pin the origin, or a later
                // cross-origin redirect hop would bind to the other host and receive the token.
                var boundOrigin = IsBoundOrigin(request, context);
                if (!boundOrigin ||
                    IsSkipRequested(request) ||
                    (!_overrideExistingAuthorization && request.Headers.Contains("Authorization")) ||
                    !IsAllowedOrigin(request.Uri))
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

        private bool IsAllowedOrigin(Uri uri)
        {
            var allowed = _allowedOrigins;
            if (allowed.Length == 0)
                return true;

            if (uri == null || !uri.IsAbsoluteUri)
                return false;

            for (int i = 0; i < allowed.Length; i++)
            {
                if (IsSameOrigin(allowed[i], uri))
                    return true;
            }

            return false;
        }

        private static Uri[] SnapshotOrigins(AuthInterceptorOptions options)
        {
            var origins = options.AllowedOrigins;
            if (origins == null || origins.Count == 0)
                return Array.Empty<Uri>();

            var snapshot = new Uri[origins.Count];
            for (int i = 0; i < snapshot.Length; i++)
            {
                var origin = origins[i];
                if (origin == null || !origin.IsAbsoluteUri)
                    throw new ArgumentException("Allowed origins must be absolute URIs.", nameof(options));
                if (!string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Allowed origins must use the http or https scheme.", nameof(options));
                if (origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" ||
                    origin.Query.Length != 0 || origin.Fragment.Length != 0)
                    throw new ArgumentException(
                        "Allowed origins are scheme, host and port only (no user info, path, query or fragment).",
                        nameof(options));
                snapshot[i] = origin;
            }

            return snapshot;
        }

        private static bool IsSameOrigin(Uri a, Uri b)
        {
            return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
                   a.Port == b.Port;
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

            return IsSameOrigin(bound, uri);
        }

        private string BuildAuthorizationValue(string token)
        {
            if (string.IsNullOrEmpty(_scheme))
                return token;

            return _scheme + " " + token;
        }
    }
}
