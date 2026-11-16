using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

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
    /// <para>
    /// Token provider waits (<see cref="IAuthTokenProvider.GetTokenAsync"/> and, for
    /// <see cref="AuthInterceptorOptions.RetryOnUnauthorized"/>,
    /// <see cref="IAuthTokenInvalidator.InvalidateTokenAsync"/>) are bounded by the request's
    /// <see cref="UHttpRequest.Timeout"/>; a provider that does not answer in time fails the request
    /// with <see cref="UHttpErrorType.Timeout"/>.
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

        // Bound for draining the body of a 401 that is retried (same bound as the retry interceptor).
        private static readonly TimeSpan RejectedResponseDiscardTimeout = TimeSpan.FromSeconds(2);

        private readonly IAuthTokenProvider _tokenProvider;

        // Set only with AuthInterceptorOptions.RetryOnUnauthorized; null disables the 401 retry.
        private readonly IAuthTokenInvalidator _invalidator;
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
            : this(tokenProvider, scheme, Array.Empty<Uri>(), overrideExistingAuthorization: false, retryOnUnauthorized: false)
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
        /// <exception cref="ArgumentException">The scheme contains CR or LF, an allowed origin is
        /// <c>null</c>, not an absolute <c>http</c>/<c>https</c> URI, or has user info, a path, a
        /// query or a fragment, or <see cref="AuthInterceptorOptions.RetryOnUnauthorized"/> is set and
        /// <paramref name="tokenProvider"/> does not implement <see cref="IAuthTokenInvalidator"/>.</exception>
        public AuthInterceptor(IAuthTokenProvider tokenProvider, AuthInterceptorOptions options)
            : this(
                tokenProvider,
                (options ?? throw new ArgumentNullException(nameof(options))).Scheme,
                SnapshotOrigins(options),
                options.OverrideExistingAuthorization,
                options.RetryOnUnauthorized)
        {
        }

        private AuthInterceptor(
            IAuthTokenProvider tokenProvider,
            string scheme,
            Uri[] allowedOrigins,
            bool overrideExistingAuthorization,
            bool retryOnUnauthorized)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            if (retryOnUnauthorized)
            {
                _invalidator = tokenProvider as IAuthTokenInvalidator ?? throw new ArgumentException(
                    "RetryOnUnauthorized requires a token provider that implements IAuthTokenInvalidator.",
                    "options");
            }

            if (scheme != null && (scheme.Contains('\r') || scheme.Contains('\n')))
                throw new ArgumentException("Auth scheme must not contain CR or LF characters", nameof(scheme));
            _scheme = scheme;
            _allowedOrigins = allowedOrigins;
            _overrideExistingAuthorization = overrideExistingAuthorization;
            _originStateKey = "TurboHTTP.Auth.AuthInterceptor.Origin." +
                              Interlocked.Increment(ref s_nextInstanceId).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Adds the token of the configured provider to requests as an <c>Authorization</c> header, only for the origins the interceptor allows (see the class remarks). Thread-safe.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
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

                var token = await GetProviderTokenAsync(request, rejectedToken: null, cancellationToken)
                    .ConfigureAwait(false);

                if (_invalidator == null || string.IsNullOrEmpty(token) ||
                    request.Content.Replayability == RequestBodyReplayability.NonReplayable)
                {
                    await DispatchWithTokenAsync(next, request, token, handler, context, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                // RFC 6750 section 3.1 / RFC 9110 section 15.5.2: a 401 to a request that carried the
                // token refuses that token. Replace it once and resend; the second answer is final.
                var detector = new UnauthorizedRetryHandler(handler, request.Uri, cancellationToken);
                await DispatchWithTokenAsync(next, request, token, detector, context, cancellationToken)
                    .ConfigureAwait(false);
                if (!detector.Rejected)
                    return;

                var replacement = await GetProviderTokenAsync(request, token, cancellationToken)
                    .ConfigureAwait(false);
                detector.BeginFinalAttempt();
                context.UpdateRequest(request);
                await DispatchWithTokenAsync(next, request, replacement, detector, context, cancellationToken)
                    .ConfigureAwait(false);
            };
        }

        private async Task DispatchWithTokenAsync(
            DispatchFunc next,
            UHttpRequest request,
            string token,
            IHttpHandler handler,
            RequestContext context,
            CancellationToken cancellationToken)
        {
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
        }

        /// <summary>
        /// Asks the provider for the token (or, with <paramref name="rejectedToken"/>, for its
        /// replacement) and waits at most the request's timeout (#744). The transport timeout starts
        /// only once the request is dispatched, so a hung provider would otherwise hang the request.
        /// The provider gets the caller's token; the bound ends only this wait (a refresh shared with
        /// other requests keeps running). No timer is created when the provider answers synchronously.
        /// </summary>
        private async Task<string> GetProviderTokenAsync(
            UHttpRequest request,
            string rejectedToken,
            CancellationToken cancellationToken)
        {
            var providerTask = CallProviderAsync(rejectedToken, cancellationToken);
            var timeout = request.Timeout;
            if (providerTask.IsCompleted || timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                return await providerTask.ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await AuthTaskWait.WaitAsync(providerTask, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                // One clause (IL2CPP, #320): the caller's cancellation and a provider's own
                // cancellation propagate unchanged; only the expired request budget becomes a timeout.
                if (cancellationToken.IsCancellationRequested || !timeoutCts.IsCancellationRequested ||
                    providerTask.IsCompleted)
                    throw;

                throw new UHttpException(new UHttpError(
                    UHttpErrorType.Timeout,
                    "The auth token provider did not return a token within the request timeout (" +
                    timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s).",
                    ex));
            }
        }

        private Task<string> CallProviderAsync(string rejectedToken, CancellationToken cancellationToken)
        {
            return rejectedToken == null
                ? _tokenProvider.GetTokenAsync(cancellationToken)
                : _invalidator.InvalidateTokenAsync(rejectedToken, cancellationToken);
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

        /// <summary>
        /// Handler for the <see cref="AuthInterceptorOptions.RetryOnUnauthorized"/> path. On the first
        /// attempt it swallows a 401 from the origin the token was attached for (discarding its body)
        /// and records it; the final attempt forwards everything. <c>OnRequestStart</c> reaches the
        /// caller's handler once. Used by one dispatch at a time, so it needs no synchronization.
        /// </summary>
        private sealed class UnauthorizedRetryHandler : IHttpHandler
        {
            private readonly IHttpHandler _inner;
            private readonly Uri _tokenOrigin;
            private readonly CancellationToken _dispatchCancellationToken;
            private bool _requestStartForwarded;
            private bool _finalAttempt;

            internal UnauthorizedRetryHandler(
                IHttpHandler inner,
                Uri tokenOrigin,
                CancellationToken dispatchCancellationToken)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _tokenOrigin = tokenOrigin;
                _dispatchCancellationToken = dispatchCancellationToken;
            }

            internal bool Rejected { get; private set; }

            internal void BeginFinalAttempt()
            {
                _finalAttempt = true;
            }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                if (_requestStartForwarded)
                    return;

                _requestStartForwarded = true;
                _inner.OnRequestStart(request, context);
            }

            public async ValueTask OnResponseStartAsync(
                int statusCode,
                HttpHeaders headers,
                IResponseBodySource body,
                RequestContext context)
            {
                if (!_finalAttempt && statusCode == 401 && IsTokenOrigin(context))
                {
                    Rejected = true;
                    await ResponseBodyDiscardHelper
                        .DiscardAsync(body, _dispatchCancellationToken, RejectedResponseDiscardTimeout)
                        .ConfigureAwait(false);
                    return;
                }

                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                _inner.OnResponseError(error, context);
            }

            // A 401 from a redirect hop to another origin is about that origin's credentials, which
            // this interceptor did not send (the hop carries no token from it): deliver it.
            private bool IsTokenOrigin(RequestContext context)
            {
                var responseUri = context?.Request?.Uri;
                if (responseUri == null || _tokenOrigin == null ||
                    !responseUri.IsAbsoluteUri || !_tokenOrigin.IsAbsoluteUri)
                    return true;

                return IsSameOrigin(_tokenOrigin, responseUri);
            }
        }
    }
}
