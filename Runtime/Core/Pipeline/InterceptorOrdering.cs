using System.Collections.Generic;

namespace TurboHTTP.Core
{
    /// <summary>
    /// The pipeline role of a first-party interceptor that has an ordering constraint (#55). Core
    /// cannot reference the optional modules, so their interceptors declare the role through
    /// <see cref="IInterceptorRole"/>.
    /// </summary>
    internal enum InterceptorRole
    {
        None = 0,
        Redirect,
        Auth,
        Cache,
        Retry,
        Concurrency
    }

    /// <summary>
    /// Implemented (explicitly) by first-party interceptors whose position in the pipeline matters.
    /// </summary>
    internal interface IInterceptorRole
    {
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
