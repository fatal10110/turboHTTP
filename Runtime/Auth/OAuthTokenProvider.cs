using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Auth token provider backed by OAuth token store + optional refresh flow.
    /// </summary>
    public sealed class OAuthTokenProvider : IAuthTokenProvider
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
            var refresh = _refreshTemplate == null
                ? null
                : new OAuthRefreshRequest
                {
                    Config = _refreshTemplate.Config,
                    RefreshToken = _refreshTemplate.RefreshToken,
                    Scope = _refreshTemplate.Scope,
                    TokenStoreKey = _refreshTemplate.TokenStoreKey
                };

            var token = await _oauthClient.GetValidTokenAsync(_tokenStoreKey, refresh, cancellationToken)
                .ConfigureAwait(false);
            return token?.AccessToken;
        }
    }
}
