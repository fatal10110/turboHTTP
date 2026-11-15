using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// The pipeline role of an interceptor whose position relative to other interceptors matters.
    /// <see cref="UHttpClient"/> checks the roles of its interceptors when it is constructed and when a
    /// plugin registers interceptors, and rejects orders known to be unsafe.
    /// </summary>
    /// <remarks>
    /// Interceptors run in list order: index 0 is the outermost (sees the request first and the response
    /// last). Rejected orders, an interceptor of the first role outside (before) one of the second:
    /// <list type="bullet">
    /// <item><see cref="Cache"/> outside <see cref="Auth"/>: the cache never sees the <c>Authorization</c>
    /// header, so per-user responses could be stored under a user-less key (RFC 9111 section 3.5).</item>
    /// <item><see cref="Cache"/> outside <see cref="Redirect"/>: the redirect target's response would be
    /// attributed to the original URL (RFC 9111 section 3).</item>
    /// <item><see cref="Cache"/> outside <see cref="Cookie"/>: the cache never sees the <c>Cookie</c>
    /// header, so a response varying on it could be served to another cookie holder (RFC 9111 section 4.1).</item>
    /// <item><see cref="Concurrency"/> outside <see cref="Retry"/>: a permit is held through every retry
    /// backoff delay.</item>
    /// </list>
    /// The numeric values are stable.
    /// </remarks>
    public enum InterceptorRole
    {
        /// <summary>No ordering constraint. The default for interceptors that do not implement <see cref="IInterceptorRole"/>.</summary>
        None = 0,

        /// <summary>Follows redirects by dispatching one request per hop (for example <c>RedirectInterceptor</c>).</summary>
        Redirect = 1,

        /// <summary>Adds credentials such as <c>Authorization</c> to the request (for example <c>AuthInterceptor</c>). Must sit outside (before) every <see cref="Cache"/> interceptor.</summary>
        Auth = 2,

        /// <summary>Serves and stores responses (for example <c>CacheInterceptor</c>). Must sit inside (after) every <see cref="Redirect"/>, <see cref="Cookie"/> and <see cref="Auth"/> interceptor.</summary>
        Cache = 3,

        /// <summary>Re-sends failed attempts (for example <c>RetryInterceptor</c>). Must sit outside (before) every <see cref="Concurrency"/> interceptor.</summary>
        Retry = 4,

        /// <summary>Limits concurrent requests with permits (for example <c>ConcurrencyInterceptor</c>).</summary>
        Concurrency = 5,

        /// <summary>Adds the <c>Cookie</c> header and stores <c>Set-Cookie</c> (for example <c>CookieInterceptor</c>). Must sit outside (before) every <see cref="Cache"/> interceptor.</summary>
        Cookie = 6
    }

    /// <summary>
    /// Declares the <see cref="InterceptorRole"/> of an <see cref="IHttpInterceptor"/> so that
    /// <see cref="UHttpClient"/> can reject unsafe interceptor orders.
    /// </summary>
    /// <remarks>
    /// Implement it on a custom interceptor that plays one of the roles (a custom cache, credential or
    /// redirect interceptor) so the client validates its position like the first-party one. A decorator
    /// that wraps another interceptor should return the wrapped interceptor's role, otherwise the wrapped
    /// interceptor is treated as <see cref="InterceptorRole.None"/> and its position is not checked.
    /// Interceptors that do not implement this interface have role <see cref="InterceptorRole.None"/>.
    /// The role is read once, when the client is constructed or a plugin registers the interceptor.
    /// </remarks>
    public interface IInterceptorRole
    {
        /// <summary>The pipeline role of this interceptor.</summary>
        InterceptorRole Role { get; }
    }

    /// <summary>
    /// Ordering rules for <see cref="UHttpClient"/> pipelines. Interceptors run in list order: index 0
    /// is the outermost (sees the request first and the response last).
    /// </summary>
    /// <remarks>
    /// Rejected combinations (an interceptor of the first role outside one of the second):
    /// <list type="bullet">
    /// <item>Cache outside Auth: the cache never sees the <c>Authorization</c> header the auth
    /// interceptor adds, so <c>AllowCacheForAuthorizedRequests = false</c> is bypassed and per-user
    /// responses are stored under a user-less key and served to the next user (RFC 9111 section 3.5).</item>
    /// <item>Cache outside Redirect: the cache looks up the original URL but the response it receives is the
    /// redirect target's, a different resource (RFC 9111 section 3, RFC 9110 section 15.4) (#494, #536).</item>
    /// <item>Cache outside Cookie: the cache never sees the <c>Cookie</c> header the cookie interceptor adds,
    /// so a response that varies on it is keyed without it (RFC 9111 section 4.1) (#536).</item>
    /// <item>Concurrency outside Retry: a concurrency permit is held through every retry backoff
    /// delay (up to <c>RetryPolicy.MaxDelay</c>), starving other requests.</item>
    /// </list>
    /// Redirect and Auth, and Redirect and Cookie, are safe in either order: the token is bound to
    /// its origin (#56) and each hop's cookies are stored for that hop's host (#58).
    /// </remarks>
    internal static class InterceptorOrdering
    {
        private struct Rule
        {
            public InterceptorRole Outer;
            public InterceptorRole Inner;
            public string Reason;
        }

        private static readonly Rule[] Rules =
        {
            new Rule
            {
                Outer = InterceptorRole.Cache,
                Inner = InterceptorRole.Auth,
                Reason = "the cache would never see the Authorization header the auth interceptor adds, " +
                         "so AllowCacheForAuthorizedRequests is bypassed and per-user responses are cached " +
                         "under a user-less key. Place the AuthInterceptor before the CacheInterceptor."
            },
            new Rule
            {
                Outer = InterceptorRole.Cache,
                Inner = InterceptorRole.Redirect,
                Reason = "the cache would look up the original URL but receive the redirect target's response, " +
                         "a different resource. Place the RedirectInterceptor before the CacheInterceptor."
            },
            new Rule
            {
                Outer = InterceptorRole.Cache,
                Inner = InterceptorRole.Cookie,
                Reason = "the cache would never see the Cookie header the cookie interceptor adds, so a " +
                         "response that varies on it is keyed without it. Place the CookieInterceptor before " +
                         "the CacheInterceptor."
            },
            new Rule
            {
                Outer = InterceptorRole.Concurrency,
                Inner = InterceptorRole.Retry,
                Reason = "a concurrency permit would be held through every retry backoff delay. " +
                         "Place the RetryInterceptor before the ConcurrencyInterceptor."
            }
        };

        internal static InterceptorRole GetRole(IHttpInterceptor interceptor)
        {
            return interceptor is IInterceptorRole role ? role.Role : InterceptorRole.None;
        }

        internal static bool Contains(IReadOnlyList<IHttpInterceptor> interceptors, InterceptorRole role)
        {
            if (interceptors == null)
                return false;

            for (int i = 0; i < interceptors.Count; i++)
            {
                if (GetRole(interceptors[i]) == role)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a description of the first rejected combination in <paramref name="interceptors"/>
        /// (outermost first), or <c>null</c> when the order is acceptable.
        /// </summary>
        internal static string FindViolation(IReadOnlyList<IHttpInterceptor> interceptors)
        {
            if (interceptors == null)
                return null;

            for (int r = 0; r < Rules.Length; r++)
            {
                var rule = Rules[r];
                int outerIndex = -1;
                for (int i = 0; i < interceptors.Count; i++)
                {
                    var role = GetRole(interceptors[i]);
                    if (role == rule.Outer && outerIndex < 0)
                    {
                        outerIndex = i;
                    }
                    else if (role == rule.Inner && outerIndex >= 0)
                    {
                        return "Interceptor order rejected: " + interceptors[outerIndex].GetType().Name +
                               " (index " + outerIndex + ") runs outside " + interceptors[i].GetType().Name +
                               " (index " + i + "); " + rule.Reason;
                    }
                }
            }

            return null;
        }
    }
}
