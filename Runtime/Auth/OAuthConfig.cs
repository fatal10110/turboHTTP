using System;
using System.Net;

namespace TurboHTTP.Auth
{
    public sealed class OAuthConfig
    {
        public string ClientId { get; set; }
        public Uri AuthorizationEndpoint { get; set; }
        public Uri TokenEndpoint { get; set; }
        public Uri RedirectUri { get; set; }
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public bool UsePkce { get; set; } = true;
        public bool UseOidcDiscovery { get; set; }
        public bool AllowInsecureEndpointsForDevelopment { get; set; }
        public bool AllowEmptyScopes { get; set; }

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

    public sealed class OAuthAuthorizationRequest
    {
        public Uri AuthorizationUri { get; set; }
        public string State { get; set; }
        public string Nonce { get; set; }
        public string CodeVerifier { get; set; }
        public string CodeChallenge { get; set; }
    }

    public sealed class OAuthCodeExchangeRequest
    {
        public OAuthConfig Config { get; set; }
        public string AuthorizationCode { get; set; }
        public string CodeVerifier { get; set; }
        public string RedirectUri { get; set; }
    }

    public sealed class OAuthRefreshRequest
    {
        public OAuthConfig Config { get; set; }
        public string RefreshToken { get; set; }
        public string Scope { get; set; }
        public string TokenStoreKey { get; set; }
    }

    public sealed class OpenIdProviderMetadata
    {
        public Uri AuthorizationEndpoint { get; set; }
        public Uri TokenEndpoint { get; set; }
        public Uri Issuer { get; set; }
    }
}
