using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Middleware
{
    /// <summary>
    /// Interceptor that adds default headers to all requests.
    /// Headers are only added if they don't already exist on the request
    /// (unless overrideExisting is true).
    /// </summary>
    /// <remarks>
    /// Credential defaults (<c>Authorization</c>, <c>Proxy-Authorization</c>, <c>Cookie</c>) are
    /// bound to the origin (scheme, host, port) of the first request this interceptor sees for a
    /// given <see cref="RequestContext"/>. When the interceptor runs inside a
    /// <see cref="RedirectInterceptor"/> and a redirect hop targets a different origin, those
    /// defaults are not applied to the hop (RFC 9110 section 15.4), so the interceptor order does
    /// not decide whether a credential leaks. Redirects back to the original origin carry them
    /// again. Other default headers are applied to every hop.
    /// </remarks>
    public sealed class DefaultHeadersInterceptor : IHttpInterceptor
    {
        private static int s_nextInstanceId;

        private readonly HttpHeaders _defaultHeaders;
        private readonly bool _overrideExisting;
        private readonly string _originStateKey;

        /// <summary>Creates the interceptor.</summary>
        /// <param name="defaultHeaders">Headers to add to every request; <c>null</c> adds none. The instance is not copied, so it should not be changed once requests are in flight.</param>
        /// <param name="overrideExisting">Whether a default replaces a header the request already has; by default the request's own value wins.</param>
        public DefaultHeadersInterceptor(HttpHeaders defaultHeaders, bool overrideExisting = false)
        {
            _defaultHeaders = defaultHeaders ?? new HttpHeaders();
            _overrideExisting = overrideExisting;
            _originStateKey = "TurboHTTP.Middleware.DefaultHeadersInterceptor.Origin." +
                              Interlocked.Increment(ref s_nextInstanceId).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Adds the default headers to each request (on a copy; the caller's request is not changed). Credential headers apply only to the origin of the first request seen for the context. Thread-safe.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="next"/> is null.</exception>
        public DispatchFunc Wrap(DispatchFunc next)
        {
            if (next == null)
                throw new ArgumentNullException(nameof(next));

            return async (request, handler, context, cancellationToken) =>
            {
                var requestForNext = request;

                if (_defaultHeaders.Count > 0)
                {
                    var boundOrigin = IsBoundOrigin(request, context);
                    foreach (var name in _defaultHeaders.Names)
                    {
                        if (!boundOrigin && IsCredentialHeader(name))
                            continue;

                        if (!_overrideExisting && request.Headers.Contains(name))
                            continue;

                        var values = _defaultHeaders.GetValues(name);
                        if (values.Count == 0)
                            continue;

                        if (ReferenceEquals(requestForNext, request))
                        {
                            requestForNext = request.CopyWithSharedContent();
                            context.UpdateRequest(requestForNext);
                        }

                        requestForNext.Headers.Set(name, values[0]);
                        for (int i = 1; i < values.Count; i++)
                            requestForNext.Headers.Add(name, values[i]);
                    }
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

        /// <summary>
        /// Headers that carry credentials and must stay with the origin they were configured for
        /// (the same set <see cref="RedirectInterceptor"/> strips on a cross-origin hop).
        /// </summary>
        private static bool IsCredentialHeader(string name)
        {
            return string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "Cookie", StringComparison.OrdinalIgnoreCase);
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

            return !RedirectInterceptor.IsCrossOrigin(bound, uri);
        }
    }
}
