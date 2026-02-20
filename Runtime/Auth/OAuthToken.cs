using System;

namespace TurboHTTP.Auth
{
    public sealed class OAuthToken
    {
        public OAuthToken(
            string accessToken,
            DateTime expiresAtUtc,
            string refreshToken = null,
            string tokenType = "Bearer",
            string idToken = null,
            string scope = null)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new ArgumentException("Access token is required.", nameof(accessToken));
            if (expiresAtUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("ExpiresAtUtc must be specified in UTC.", nameof(expiresAtUtc));

            AccessToken = accessToken;
            ExpiresAtUtc = expiresAtUtc;
            RefreshToken = refreshToken;
            TokenType = string.IsNullOrWhiteSpace(tokenType) ? "Bearer" : tokenType;
            IdToken = idToken;
            Scope = scope;
        }

        public string AccessToken { get; }
        public string RefreshToken { get; }
        public string TokenType { get; }
        public DateTime ExpiresAtUtc { get; }
        public string IdToken { get; }
        public string Scope { get; }

        public bool IsExpired(DateTime utcNow, TimeSpan refreshSkew)
        {
            return utcNow >= (ExpiresAtUtc - refreshSkew);
        }
    }
}
