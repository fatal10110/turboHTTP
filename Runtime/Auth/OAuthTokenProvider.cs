using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Auth token provider backed by OAuth token store + optional refresh flow.
    /// </summary>
    /// <remarks>
    /// As an <see cref="IAuthTokenInvalidator"/> it refreshes a rejected access token through
    /// <see cref="OAuthClient.ForceRefreshTokenAsync"/> when it has a refresh template.
    /// </remarks>
    public sealed class OAuthTokenProvider : IAuthTokenProvider, IAuthTokenInvalidator
    {
        private readonly OAuthClient _oauthClient;
        private readonly string _tokenStoreKey;
        private readonly OAuthRefreshRequest _refreshTemplate;

        public OAuthTokenProvider(
            OAuthClient oauthClient,
            string tokenStoreKey,
            OAuthRefreshRequest refreshTemplate = null)
        {
            _oauthClient = oauthClient ?? throw new ArgumentNullException(nameof(oauthClient));
            _tokenStoreKey = tokenStoreKey ?? throw new ArgumentNullException(nameof(tokenStoreKey));
            _refreshTemplate = refreshTemplate;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            var token = await _oauthClient.GetValidTokenAsync(_tokenStoreKey, CopyTemplate(), cancellationToken)
                .ConfigureAwait(false);
            return token?.AccessToken;
        }

        /// <summary>
        /// Refreshes the stored token through the token endpoint when its access token is still
        /// <paramref name="rejectedToken"/>, and returns the current access token. Without a refresh
        /// template nothing can be refreshed and the stored access token is returned.
        /// </summary>
        /// <param name="rejectedToken">The access token the server rejected; <c>null</c> or empty
        /// refreshes unconditionally.</param>
        /// <param name="cancellationToken">Cancels this wait; a refresh shared with other callers
        /// continues.</param>
        /// <exception cref="InvalidOperationException">The refresh token is missing or was rejected;
        /// reauthorization is required.</exception>
        public async Task<string> InvalidateTokenAsync(
            string rejectedToken,
            CancellationToken cancellationToken = default)
        {
            if (_refreshTemplate == null)
                return await GetTokenAsync(cancellationToken).ConfigureAwait(false);

            var token = await _oauthClient
                .ForceRefreshTokenAsync(_tokenStoreKey, CopyTemplate(), rejectedToken, cancellationToken)
                .ConfigureAwait(false);
            return token?.AccessToken;
        }

        private OAuthRefreshRequest CopyTemplate()
        {
            return _refreshTemplate == null
                ? null
                : new OAuthRefreshRequest
                {
                    Config = _refreshTemplate.Config,
                    RefreshToken = _refreshTemplate.RefreshToken,
                    Scope = _refreshTemplate.Scope,
                    TokenStoreKey = _refreshTemplate.TokenStoreKey
                };
        }
    }
}
