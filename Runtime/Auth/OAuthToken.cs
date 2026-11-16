using System;

namespace TurboHTTP.Auth
{
    /// <summary>An immutable OAuth token set (RFC 6749 section 5.1). Thread-safe. It holds secrets: never log it.</summary>
    public sealed class OAuthToken
    {
        /// <summary>Creates a token.</summary>
        /// <exception cref="ArgumentException"><paramref name="accessToken"/> is blank, or <paramref name="expiresAtUtc"/> is not UTC.</exception>
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

        /// <summary>The access token; never blank.</summary>
        public string AccessToken { get; }
        /// <summary>The refresh token, or <c>null</c> when none was issued.</summary>
        public string RefreshToken { get; }
        /// <summary>The token type, <c>Bearer</c> unless given.</summary>
        public string TokenType { get; }
        /// <summary>When the access token expires, in UTC.</summary>
        public DateTime ExpiresAtUtc { get; }
        /// <summary>The OpenID Connect ID token, or <c>null</c>. It is not validated by this library.</summary>
        public string IdToken { get; }
        /// <summary>The scope the server granted, or <c>null</c> when it did not say.</summary>
        public string Scope { get; }

        /// <summary>Whether the token is expired or will expire within <paramref name="refreshSkew"/> of <paramref name="utcNow"/>.</summary>
        public bool IsExpired(DateTime utcNow, TimeSpan refreshSkew)
        {
            return utcNow >= (ExpiresAtUtc - refreshSkew);
        }
    }
}
