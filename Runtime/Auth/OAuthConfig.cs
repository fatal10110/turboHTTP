using System;
using System.Net;

namespace TurboHTTP.Auth
{
    /// <summary>
    /// Configuration settings for OAuth 2.0 flows.
    /// </summary>
    public sealed class OAuthConfig
    {
        /// <summary> Gets or sets the OAuth client identifier. </summary>
        public string ClientId { get; set; }
        /// <summary> Gets or sets the authorization endpoint URI. </summary>
        public Uri AuthorizationEndpoint { get; set; }
        /// <summary> Gets or sets the token endpoint URI. </summary>
        public Uri TokenEndpoint { get; set; }
        /// <summary> Gets or sets the redirect URI used in authorization flows. </summary>
        public Uri RedirectUri { get; set; }
        /// <summary> Gets or sets the scopes to request. </summary>
        public string[] Scopes { get; set; } = Array.Empty<string>();
        /// <summary> Gets or sets whether to use PKCE (Proof Key for Code Exchange). Default is true. </summary>
        public bool UsePkce { get; set; } = true;
        /// <summary> Gets or sets whether to attempt discovering endpoints via OIDC discovery. </summary>
        public bool UseOidcDiscovery { get; set; }
        /// <summary> Gets or sets whether to allow non-HTTPS endpoints for development purposes. </summary>
        public bool AllowInsecureEndpointsForDevelopment { get; set; }
        /// <summary> Gets or sets whether empty scopes are permitted. </summary>
        public bool AllowEmptyScopes { get; set; }

        /// <summary> Validates that the configuration is complete and correct. </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(ClientId))
                throw new ArgumentException("OAuth ClientId is required.", nameof(ClientId));

            if (AuthorizationEndpoint == null)
                throw new ArgumentNullException(nameof(AuthorizationEndpoint));
            if (TokenEndpoint == null)
                throw new ArgumentNullException(nameof(TokenEndpoint));
            if (RedirectUri == null)
                throw new ArgumentNullException(nameof(RedirectUri));

            ValidateHttpsEndpoint(AuthorizationEndpoint, nameof(AuthorizationEndpoint), AllowInsecureEndpointsForDevelopment);
            ValidateHttpsEndpoint(TokenEndpoint, nameof(TokenEndpoint), AllowInsecureEndpointsForDevelopment);

            if (!RedirectUri.IsAbsoluteUri)
                throw new ArgumentException("RedirectUri must be absolute.", nameof(RedirectUri));

            if (!AllowEmptyScopes && (Scopes == null || Scopes.Length == 0))
                throw new ArgumentException("At least one scope is required.", nameof(Scopes));
        }

        private static void ValidateHttpsEndpoint(Uri uri, string paramName, bool allowInsecure)
        {
            if (uri == null)
                throw new ArgumentNullException(paramName);

            if (allowInsecure)
                return;

            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) && !IsLocalhostUri(uri))
            {
                throw new ArgumentException($"{paramName} must use HTTPS (except localhost development endpoints).", paramName);
            }
        }

        internal static bool IsLocalhostUri(Uri uri)
        {
            if (uri == null)
                return false;

            if (uri.IsLoopback)
                return true;

            var host = uri.Host;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip))
                return true;

            return false;
        }
    }

    /// <summary> Represents a request to an OAuth authorization endpoint. </summary>
    public sealed class OAuthAuthorizationRequest
    {
        /// <summary>The URL to open in the user's browser. Read-only.</summary>
        public Uri AuthorizationUri { get; internal set; }
        /// <summary>The <c>state</c> value in <see cref="AuthorizationUri"/>; compare it with the returned one using <see cref="OAuthClient.ValidateState"/>. Read-only.</summary>
        public string State { get; internal set; }
        /// <summary>The OpenID Connect <c>nonce</c>, when requested; validate it against the ID token. Read-only.</summary>
        public string Nonce { get; internal set; }
        /// <summary>The PKCE code verifier (RFC 7636); keep it secret and pass it to the code exchange. Read-only.</summary>
        public string CodeVerifier { get; internal set; }
        /// <summary>The PKCE S256 code challenge sent in <see cref="AuthorizationUri"/>. Read-only.</summary>
        public string CodeChallenge { get; internal set; }
    }

    /// <summary> Represents a request to exchange an authorization code for tokens. </summary>
    public sealed class OAuthCodeExchangeRequest
    {
        /// <summary>The client configuration used for the request.</summary>
        public OAuthConfig Config { get; set; }
        /// <summary>The authorization code returned to the redirect URI.</summary>
        public string AuthorizationCode { get; set; }
        /// <summary>The PKCE code verifier (RFC 7636) that matches the challenge sent in the authorization request; <c>null</c> when PKCE is not used.</summary>
        public string CodeVerifier { get; set; }
        /// <summary>The redirect URI used in the authorization request; must match it exactly.</summary>
        public string RedirectUri { get; set; }
    }

    /// <summary> Represents a request to refresh an existing OAuth token. </summary>
    public sealed class OAuthRefreshRequest
    {
        /// <summary>The client configuration used for the request.</summary>
        public OAuthConfig Config { get; set; }
        /// <summary>The refresh token to present to the token endpoint.</summary>
        public string RefreshToken { get; set; }
        /// <summary>Space-delimited scope sent with the refresh request when not blank; it is also part of the default token-store key.</summary>
        public string Scope { get; set; }
        /// <summary>Key of the stored token to update; when blank it is derived with <see cref="OAuthClient.BuildTokenStoreKey"/> from the config and <see cref="Scope"/>.</summary>
        public string TokenStoreKey { get; set; }
    }

    /// <summary> Stores OpenID Provider discovery metadata. </summary>
    public sealed class OpenIdProviderMetadata
    {
        /// <summary>The <c>authorization_endpoint</c> of the provider; <c>null</c> when it did not publish one. Read-only.</summary>
        public Uri AuthorizationEndpoint { get; internal set; }
        /// <summary>The <c>token_endpoint</c> of the provider; <c>null</c> when it did not publish one. Read-only.</summary>
        public Uri TokenEndpoint { get; internal set; }
        /// <summary>The <c>issuer</c> identifier of the provider; <c>null</c> when it did not publish one. Read-only.</summary>
        public Uri Issuer { get; internal set; }
    }
}
