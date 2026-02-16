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
    }
}
