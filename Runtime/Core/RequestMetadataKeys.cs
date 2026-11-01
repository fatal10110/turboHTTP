namespace TurboHTTP.Core
{
    /// <summary>
    /// Reserved metadata keys used by built-in interceptors.
    /// </summary>
    public static class RequestMetadataKeys
    {
        public const string FollowRedirects = "turbohttp.follow_redirects";
        public const string MaxRedirects = "turbohttp.max_redirects";
        public const string IsCrossSiteRequest = "turbohttp.is_cross_site";
        public const string AllowHttpsToHttpDowngrade = "turbohttp.allow_https_to_http_downgrade";
        public const string EnforceRedirectTotalTimeout = "turbohttp.enforce_redirect_total_timeout";
        public const string ExplicitTimeout = "turbohttp.explicit_timeout";
        public const string ProxySettings = "turbohttp.proxy.settings";
        public const string ProxyAbsoluteForm = "turbohttp.proxy.absolute_form";
        public const string ProxyDisabled = "turbohttp.proxy.disabled";

        /// <summary>
        /// <see cref="bool"/>: whether the transport may send this request in cleartext (an
        /// <c>http://</c> target or an <c>http://</c> proxy leg). <see cref="UHttpClient"/> sets
        /// it from <see cref="UHttpClientOptions.AllowCleartext"/>; when absent the transport uses
        /// <see cref="CleartextTrafficPolicy.DefaultAllowCleartext"/>.
        /// </summary>
        public const string AllowCleartext = "turbohttp.transport.allow_cleartext";
        public const string BackgroundReplayDedupeKey = "turbohttp.background.replay_dedupe_key";
    }
}
