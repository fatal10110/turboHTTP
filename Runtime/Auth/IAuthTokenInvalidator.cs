using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Optional companion to <see cref="IAuthTokenProvider"/> for providers that can replace a token
    /// a server rejected. <see cref="AuthInterceptor"/> uses it when
    /// <see cref="AuthInterceptorOptions.RetryOnUnauthorized"/> is set: after a <c>401</c> response
    /// to a request that carried the provider's token, it calls <see cref="InvalidateTokenAsync"/>
    /// and sends the request once more with the returned token (RFC 6750 section 3.1).
    /// </summary>
    /// <remarks>
    /// <see cref="OAuthTokenProvider"/> implements it by refreshing through the token endpoint.
    /// </remarks>
    public interface IAuthTokenInvalidator
    {
        /// <summary>
        /// Discards <paramref name="rejectedToken"/> and returns the token to use instead.
        /// When the provider has already replaced <paramref name="rejectedToken"/> (another request
        /// saw the same rejection), it returns the current token without fetching a new one.
        /// </summary>
        /// <param name="rejectedToken">The token the server rejected, as returned by
        /// <see cref="IAuthTokenProvider.GetTokenAsync"/>.</param>
        /// <param name="cancellationToken">Cancels the wait for the replacement.</param>
        /// <returns>The replacement token; <c>null</c> or empty to send the retry without one.</returns>
        Task<string> InvalidateTokenAsync(string rejectedToken, CancellationToken cancellationToken = default);
    }
}
