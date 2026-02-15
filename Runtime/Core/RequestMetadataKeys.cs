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
    }
}
