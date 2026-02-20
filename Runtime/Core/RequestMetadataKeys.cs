namespace TurboHTTP.Core
{
    /// <summary>
    /// Reserved metadata keys used by built-in middleware.
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
        public const string BackgroundReplayDedupeKey = "turbohttp.background.replay_dedupe_key";
    }
}
