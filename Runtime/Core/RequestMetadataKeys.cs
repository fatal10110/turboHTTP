namespace TurboHTTP.Core
{
    /// <summary>
    /// Reserved metadata keys used by built-in interceptors.
    /// </summary>
    public static class RequestMetadataKeys
    {
        /// <summary><see cref="bool"/>: whether redirects are followed. <see cref="UHttpClient"/> sets it from <see cref="UHttpClientOptions.FollowRedirects"/> unless the request sets it.</summary>
        public const string FollowRedirects = "turbohttp.follow_redirects";
        /// <summary><see cref="int"/>: maximum redirect hops. <see cref="UHttpClient"/> sets it from <see cref="UHttpClientOptions.MaxRedirects"/> unless the request sets it.</summary>
        public const string MaxRedirects = "turbohttp.max_redirects";
        /// <summary><see cref="bool"/>: set by the redirect interceptor on a hop to another origin; the cookie interceptor uses it for SameSite decisions.</summary>
        public const string IsCrossSiteRequest = "turbohttp.is_cross_site";
        /// <summary><see cref="bool"/>: whether a redirect from <c>https://</c> to <c>http://</c> is followed. Absent means the interceptor's default (<c>false</c> unless configured).</summary>
        public const string AllowHttpsToHttpDowngrade = "turbohttp.allow_https_to_http_downgrade";
        /// <summary><see cref="bool"/>: whether the request timeout bounds the whole redirect chain instead of each hop. Absent means the interceptor's default.</summary>
        public const string EnforceRedirectTotalTimeout = "turbohttp.enforce_redirect_total_timeout";
        /// <summary><see cref="bool"/>: set when the caller chose a timeout on the request, so adaptive mobile policies do not override it.</summary>
        public const string ExplicitTimeout = "turbohttp.explicit_timeout";
        /// <summary>
        /// <see cref="TurboHTTP.Core.ProxySettings"/>: the proxy for this request (set by
        /// <see cref="ProxyRequestBuilderExtensions.WithProxy"/> or resolved by the client). Absent means
        /// a direct connection. Credentials in it go only to the proxy hop.
        /// </summary>
        public const string ProxySettings = "turbohttp.proxy.settings";
        /// <summary><see cref="bool"/>: set by the transport when it sends the request line in absolute form (RFC 9112 section 3.2.2) to an <c>http://</c> proxy; <c>false</c> inside a CONNECT tunnel. Not meant to be set by callers.</summary>
        public const string ProxyAbsoluteForm = "turbohttp.proxy.absolute_form";
        /// <summary><see cref="bool"/>: the request bypasses every proxy, including the environment (<see cref="ProxyRequestBuilderExtensions.WithoutProxy"/>).</summary>
        public const string ProxyDisabled = "turbohttp.proxy.disabled";

        /// <summary>
        /// <see cref="ClientProxyPolicy"/>: the client proxy configuration behind a
        /// <see cref="ProxySettings"/> value <see cref="UHttpClient"/> resolved itself, so a dispatch to
        /// another URI (a redirect hop) re-resolves it for that URI (#832).
        /// </summary>
        internal const string ClientProxyPolicy = "turbohttp.proxy.client_policy";

        /// <summary>
        /// <see cref="bool"/>: whether the transport may send this request in cleartext (an
        /// <c>http://</c> target or an <c>http://</c> proxy leg). <see cref="UHttpClient"/> sets
        /// it from <see cref="UHttpClientOptions.AllowCleartext"/> on every request it sends that does
        /// not set it itself, including requests built with the public <see cref="UHttpRequest"/>
        /// constructor; when absent (a transport used without a client) the transport uses
        /// <see cref="CleartextTrafficPolicy.DefaultAllowCleartext"/>.
        /// </summary>
        public const string AllowCleartext = "turbohttp.transport.allow_cleartext";
        /// <summary><see cref="string"/>: deduplication key for a request replayed after the app returns from the background (mobile background networking).</summary>
        public const string BackgroundReplayDedupeKey = "turbohttp.background.replay_dedupe_key";
    }
}
