namespace TurboHTTP.Core
{
    /// <summary>
    /// Reserved metadata keys used by built-in interceptors.
    /// </summary>
    public static class RequestMetadataKeys
    {
        /// <summary>
        /// <see cref="bool"/>: whether the redirect interceptor follows redirects for this request. <see cref="UHttpClient"/> sets it from <see cref="UHttpClientOptions.FollowRedirects"/> unless the request already carries it.
        /// </summary>
        public const string FollowRedirects = "turbohttp.follow_redirects";
        /// <summary>
        /// <see cref="int"/>: the redirect hop limit. <see cref="UHttpClient"/> sets it from <see cref="UHttpClientOptions.MaxRedirects"/> unless the request already carries it.
        /// </summary>
        public const string MaxRedirects = "turbohttp.max_redirects";
        /// <summary>
        /// <see cref="bool"/>: set by the redirect interceptor on a hop to another origin, so cookie handling can apply cross-site rules. Do not set it yourself.
        /// </summary>
        public const string IsCrossSiteRequest = "turbohttp.is_cross_site";
        /// <summary>
        /// <see cref="bool"/>: when <c>true</c>, a redirect from an <c>https</c> to an <c>http</c> URI is followed. Default: refused.
        /// </summary>
        public const string AllowHttpsToHttpDowngrade = "turbohttp.allow_https_to_http_downgrade";
        /// <summary>
        /// <see cref="bool"/>: when <c>true</c>, the request timeout bounds the whole redirect chain instead of each hop. Default: <c>true</c> (enforced); <c>false</c> gives each hop its own timeout.
        /// </summary>
        public const string EnforceRedirectTotalTimeout = "turbohttp.enforce_redirect_total_timeout";
        /// <summary>
        /// <see cref="bool"/>: set when the caller chose the request timeout explicitly, so adaptive timeout adjustment leaves it alone.
        /// </summary>
        public const string ExplicitTimeout = "turbohttp.explicit_timeout";
        /// <summary>
        /// <see cref="TurboHTTP.Core.ProxySettings"/>: the proxy for this request, overriding the client proxy. Set by <see cref="UHttpClient"/> or the proxy request builder extensions.
        /// </summary>
        public const string ProxySettings = "turbohttp.proxy.settings";
        /// <summary>
        /// <see cref="bool"/>: set by the transport when the request goes to an HTTP proxy in absolute-form (RFC 9112 section 3.2.2). Do not set it yourself.
        /// </summary>
        public const string ProxyAbsoluteForm = "turbohttp.proxy.absolute_form";
        /// <summary>
        /// <see cref="bool"/>: when <c>true</c>, the request connects directly even if a client or environment proxy exists. Wins over <see cref="ProxySettings"/>.
        /// </summary>
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
        /// <summary>
        /// <see cref="string"/>: key used to deduplicate a request replayed after a mobile background suspension.
        /// </summary>
        public const string BackgroundReplayDedupeKey = "turbohttp.background.replay_dedupe_key";
    }
}
