using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Extension methods for adding authentication support to <see cref="UHttpRequest"/>.
    /// </summary>
    public static class AuthBuilderExtensions
    {
        /// <summary>
        /// Set the Authorization header to a Bearer token.
        /// </summary>
        public static UHttpRequest WithBearerToken(this UHttpRequest request, string token)
        {
            return request.WithHeader("Authorization", $"Bearer {token}");
        }
    }
}
