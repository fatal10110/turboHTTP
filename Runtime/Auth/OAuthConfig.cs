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
        public Uri AuthorizationUri { get; set; }
        public string State { get; set; }
        public string Nonce { get; set; }
        public string CodeVerifier { get; set; }
        public string CodeChallenge { get; set; }
    }

    /// <summary> Represents a request to exchange an authorization code for tokens. </summary>
    public sealed class OAuthCodeExchangeRequest
    {
        public OAuthConfig Config { get; set; }
        public string AuthorizationCode { get; set; }
        public string CodeVerifier { get; set; }
        public string RedirectUri { get; set; }
    }

    /// <summary> Represents a request to refresh an existing OAuth token. </summary>
    public sealed class OAuthRefreshRequest
    {
        public OAuthConfig Config { get; set; }
        public string RefreshToken { get; set; }
        public string Scope { get; set; }
        public string TokenStoreKey { get; set; }
    }

    /// <summary> Stores OpenID Provider discovery metadata. </summary>
    public sealed class OpenIdProviderMetadata
    {
        public Uri AuthorizationEndpoint { get; set; }
        public Uri TokenEndpoint { get; set; }
        public Uri Issuer { get; set; }
    }
}
