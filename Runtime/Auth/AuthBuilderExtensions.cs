using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Extension methods for adding authentication support to <see cref="UHttpRequestBuilder"/>.
    /// </summary>
    public static class AuthBuilderExtensions
    {
        /// <summary>
        /// Set the Authorization header to a Bearer token.
        /// </summary>
        public static UHttpRequestBuilder WithBearerToken(this UHttpRequestBuilder builder, string token)
        {
            return builder.WithHeader("Authorization", $"Bearer {token}");
        }
    }
}
