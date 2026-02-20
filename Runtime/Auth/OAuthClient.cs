using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Auth
{
    public sealed class OAuthClient : IDisposable
    {
        private readonly UHttpClient _client;
        private readonly bool _ownsClient;
        private readonly ITokenStore _tokenStore;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _defaultRefreshSkew;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGuards
            = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.Ordinal);
        private int _disposed;

        public OAuthClient(
            UHttpClient client = null,
            ITokenStore tokenStore = null,
            Func<DateTime> utcNow = null,
            TimeSpan? defaultRefreshSkew = null)
        {
            _client = client ?? new UHttpClient();
            _ownsClient = client == null;
            _tokenStore = tokenStore ?? new InMemoryTokenStore();
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _defaultRefreshSkew = defaultRefreshSkew ?? TimeSpan.FromMinutes(1);
        }

        public Task<OAuthAuthorizationRequest> CreateAuthorizationRequestAsync(
            OAuthConfig config,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();
            ct.ThrowIfCancellationRequested();

            var state = GenerateOpaqueToken(32);
            var nonce = ContainsScope(config.Scopes, "openid") ? GenerateOpaqueToken(24) : null;

            string verifier = null;
            string challenge = null;
            if (config.UsePkce)
            {
                verifier = PkceUtility.GenerateCodeVerifier();
                challenge = PkceUtility.CreateS256CodeChallenge(verifier);
            }

            var query = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["response_type"] = "code",
                ["client_id"] = config.ClientId,
                ["redirect_uri"] = config.RedirectUri.ToString(),
                ["scope"] = string.Join(" ", config.Scopes ?? Array.Empty<string>()),
                ["state"] = state
            };

            if (nonce != null)
                query["nonce"] = nonce;

            if (config.UsePkce)
            {
                query["code_challenge"] = challenge;
                query["code_challenge_method"] = "S256";
            }

            var uri = AppendQuery(config.AuthorizationEndpoint, query);
            return Task.FromResult(new OAuthAuthorizationRequest
            {
                AuthorizationUri = uri,
                State = state,
                Nonce = nonce,
                CodeVerifier = verifier,
                CodeChallenge = challenge
            });
        }

        public async Task<OAuthConfig> ResolveEndpointsAsync(
            OAuthConfig config,
            Uri discoveryEndpoint,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (!config.UseOidcDiscovery)
                return config;

            var metadata = await DiscoverAsync(discoveryEndpoint, ct).ConfigureAwait(false);
            ValidateDiscoveredIssuer(discoveryEndpoint, metadata.Issuer);

            var resolved = CloneConfig(config);
            if (metadata.AuthorizationEndpoint != null)
                resolved.AuthorizationEndpoint = metadata.AuthorizationEndpoint;
            if (metadata.TokenEndpoint != null)
                resolved.TokenEndpoint = metadata.TokenEndpoint;

            resolved.Validate();
            return resolved;
        }

        public async Task<OAuthToken> ExchangeCodeAsync(
            OAuthCodeExchangeRequest request,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Config == null) throw new ArgumentException("Config is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.AuthorizationCode))
                throw new ArgumentException("Authorization code is required.", nameof(request));

            request.Config.Validate();

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "authorization_code",
                ["code"] = request.AuthorizationCode,
                ["client_id"] = request.Config.ClientId,
                ["redirect_uri"] = request.RedirectUri ?? request.Config.RedirectUri.ToString()
            };

            if (!string.IsNullOrEmpty(request.CodeVerifier))
                form["code_verifier"] = request.CodeVerifier;

            var response = await SendTokenRequestAsync(request.Config.TokenEndpoint, form, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = ParseTokenResponse(response.GetBodyAsString());
            ValidateOidcTokenPresence(request.Config, token);
            return token;
        }

        public async Task<OAuthToken> RefreshTokenAsync(
            OAuthRefreshRequest request,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Config == null) throw new ArgumentException("Config is required.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.RefreshToken))
                throw new ArgumentException("Refresh token is required.", nameof(request));

            request.Config.Validate();

            var key = string.IsNullOrWhiteSpace(request.TokenStoreKey)
                ? BuildTokenStoreKey(request.Config, request.Scope)
                : request.TokenStoreKey;
            var gate = _refreshGuards.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                var existing = await _tokenStore.GetAsync(key, ct).ConfigureAwait(false);
                if (existing != null && !existing.IsExpired(_utcNow(), _defaultRefreshSkew))
                    return existing;

                var form = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = request.RefreshToken,
                    ["client_id"] = request.Config.ClientId
                };

                if (!string.IsNullOrWhiteSpace(request.Scope))
                    form["scope"] = request.Scope;

                var response = await SendTokenRequestAsync(request.Config.TokenEndpoint, form, ct)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var body = response.GetBodyAsString() ?? string.Empty;
                    if (response.StatusCode == HttpStatusCode.BadRequest &&
                        body.IndexOf("invalid_grant", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        await _tokenStore.RemoveAsync(key, ct).ConfigureAwait(false);
                        throw new InvalidOperationException("Refresh token invalid. Reauthorization is required.");
                    }

                    response.EnsureSuccessStatusCode();
                }

                var token = ParseTokenResponse(response.GetBodyAsString());
                await _tokenStore.SetAsync(key, token, ct).ConfigureAwait(false);
                return token;
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task<OpenIdProviderMetadata> DiscoverAsync(Uri discoveryEndpoint, CancellationToken ct)
        {
            ThrowIfDisposed();
            if (discoveryEndpoint == null) throw new ArgumentNullException(nameof(discoveryEndpoint));
            if (!string.Equals(discoveryEndpoint.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                !OAuthConfig.IsLocalhostUri(discoveryEndpoint))
            {
                throw new ArgumentException("OIDC discovery endpoint must use HTTPS.", nameof(discoveryEndpoint));
            }

            var response = await _client.Get(discoveryEndpoint.ToString()).SendAsync(ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var data = DeserializeToDictionary(response.GetBodyAsString());
            var metadata = new OpenIdProviderMetadata
            {
                AuthorizationEndpoint = CreateUriIfPresent(data, "authorization_endpoint"),
                TokenEndpoint = CreateUriIfPresent(data, "token_endpoint"),
                Issuer = CreateUriIfPresent(data, "issuer")
            };

            return metadata;
        }

        public async Task<OAuthToken> GetValidTokenAsync(
            string key,
            OAuthRefreshRequest refreshRequest,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Token store key is required.", nameof(key));

            var token = await _tokenStore.GetAsync(key, ct).ConfigureAwait(false);
            if (token == null)
                return null;

            if (!token.IsExpired(_utcNow(), _defaultRefreshSkew))
                return token;

            if (refreshRequest == null || string.IsNullOrWhiteSpace(token.RefreshToken))
                return token;

            refreshRequest.RefreshToken = token.RefreshToken;
            refreshRequest.TokenStoreKey = key;

            return await RefreshTokenAsync(refreshRequest, ct).ConfigureAwait(false);
        }

        public static string BuildTokenStoreKey(OAuthConfig config, string audienceOrScope)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var audience = audienceOrScope ?? string.Empty;
            return config.ClientId + "|" + config.TokenEndpoint + "|" + audience;
        }

        public bool IsTokenExpired(OAuthToken token)
        {
            if (token == null)
                return true;

            return token.IsExpired(_utcNow(), _defaultRefreshSkew);
        }

        public static void ValidateState(string expectedState, string actualState)
        {
            if (string.IsNullOrEmpty(expectedState))
                throw new ArgumentException("Expected state is required.", nameof(expectedState));

            if (!string.Equals(expectedState, actualState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("OAuth state validation failed.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            if (_ownsClient)
                _client.Dispose();

            // Do not dispose semaphore instances here: refresh operations may still
            // be completing on other threads, and disposal can race with Release().
            // Keep the dictionary intact so in-flight operations can release safely.
        }

        private async Task<UHttpResponse> SendTokenRequestAsync(
            Uri endpoint,
            IDictionary<string, string> formFields,
            CancellationToken ct)
        {
            var formBody = BuildFormBody(formFields);
            var response = await _client
                .Post(endpoint.ToString())
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithBody(formBody)
                .SendAsync(ct)
                .ConfigureAwait(false);
            return response;
        }

        private OAuthToken ParseTokenResponse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidOperationException("Token endpoint returned an empty payload.");

            var data = DeserializeToDictionary(json);
            var accessToken = GetString(data, "access_token");
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new InvalidOperationException("Token response missing access_token.");

            var tokenType = GetString(data, "token_type");
            if (string.IsNullOrWhiteSpace(tokenType))
                tokenType = "Bearer";
            else if (!string.Equals(tokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Unsupported token_type '{tokenType}'. Only Bearer tokens are supported.");

            var expiresIn = GetNumber(data, "expires_in", fallback: 3600);

            return new OAuthToken(
                accessToken: accessToken,
                expiresAtUtc: _utcNow().AddSeconds(expiresIn),
                refreshToken: GetString(data, "refresh_token"),
                tokenType: "Bearer",
                idToken: GetString(data, "id_token"),
                scope: GetString(data, "scope"));
        }

        private static string BuildFormBody(IDictionary<string, string> fields)
        {
            var sb = new StringBuilder();
            var first = true;
            foreach (var pair in fields)
            {
                if (!first)
                    sb.Append('&');
                first = false;

                sb.Append(Uri.EscapeDataString(pair.Key ?? string.Empty));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }

            return sb.ToString();
        }

        private static Uri AppendQuery(Uri baseUri, IDictionary<string, string> query)
        {
            var sb = new StringBuilder();
            var hasQuery = !string.IsNullOrEmpty(baseUri.Query);
            sb.Append(baseUri.GetLeftPart(UriPartial.Path));

            if (hasQuery)
            {
                sb.Append(baseUri.Query);
                sb.Append('&');
            }
            else
            {
                sb.Append('?');
            }

            var first = true;
            foreach (var pair in query)
            {
                if (!first)
                    sb.Append('&');
                first = false;

                sb.Append(Uri.EscapeDataString(pair.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(pair.Value ?? string.Empty));
            }

            return new Uri(sb.ToString());
        }

        private static bool ContainsScope(string[] scopes, string expected)
        {
            if (scopes == null || expected == null)
                return false;

            for (int i = 0; i < scopes.Length; i++)
            {
                if (string.Equals(scopes[i], expected, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string GenerateOpaqueToken(int byteCount)
        {
            var bytes = new byte[byteCount];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        private static Dictionary<string, object> DeserializeToDictionary(string json)
        {
            var result = ProjectJsonBridge.Deserialize(
                json,
                typeof(Dictionary<string, object>),
                requiredBy: "OAuth token parsing");
            return result as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
        }

        private static OAuthConfig CloneConfig(OAuthConfig source)
        {
            if (source == null)
                return null;

            return new OAuthConfig
            {
                ClientId = source.ClientId,
                AuthorizationEndpoint = source.AuthorizationEndpoint,
                TokenEndpoint = source.TokenEndpoint,
                RedirectUri = source.RedirectUri,
                Scopes = source.Scopes != null ? (string[])source.Scopes.Clone() : Array.Empty<string>(),
                UsePkce = source.UsePkce,
                UseOidcDiscovery = source.UseOidcDiscovery,
                AllowInsecureEndpointsForDevelopment = source.AllowInsecureEndpointsForDevelopment,
                AllowEmptyScopes = source.AllowEmptyScopes
            };
        }

        private static string GetString(IReadOnlyDictionary<string, object> data, string key)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
                return null;
            return value.ToString();
        }

        private static int GetNumber(IReadOnlyDictionary<string, object> data, string key, int fallback)
        {
            if (!data.TryGetValue(key, out var value) || value == null)
                return fallback;

            if (value is int intValue)
                return intValue;
            if (value is long longValue)
                return (int)Math.Min(int.MaxValue, Math.Max(0, longValue));
            if (value is double doubleValue)
                return (int)Math.Max(0, Math.Round(doubleValue));

            if (int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                return parsed;

            return fallback;
        }

        private static Uri CreateUriIfPresent(IReadOnlyDictionary<string, object> data, string key)
        {
            var value = GetString(data, key);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
        }

        private static void ValidateOidcTokenPresence(OAuthConfig config, OAuthToken token)
        {
            if (config == null || token == null)
                return;

            if (!ContainsScope(config.Scopes, "openid"))
                return;

            if (string.IsNullOrWhiteSpace(token.IdToken))
                throw new InvalidOperationException("id_token is required for openid scope.");
        }

        private static void ValidateDiscoveredIssuer(Uri discoveryEndpoint, Uri issuer)
        {
            if (issuer == null)
                return;

            var expected = BuildExpectedIssuerFromDiscoveryEndpoint(discoveryEndpoint);
            if (expected == null)
                return;

            if (!AreEquivalentIssuers(expected, issuer))
            {
                throw new InvalidOperationException(
                    $"OIDC discovery issuer mismatch. Expected '{expected}', got '{issuer}'.");
            }
        }

        private static Uri BuildExpectedIssuerFromDiscoveryEndpoint(Uri discoveryEndpoint)
        {
            if (discoveryEndpoint == null || !discoveryEndpoint.IsAbsoluteUri)
                return null;

            const string marker = "/.well-known/openid-configuration";
            var path = discoveryEndpoint.AbsolutePath ?? string.Empty;
            var markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
            var issuerPath = markerIndex >= 0
                ? path.Substring(0, markerIndex)
                : path;

            var builder = new UriBuilder(discoveryEndpoint.Scheme, discoveryEndpoint.Host, discoveryEndpoint.Port)
            {
                Path = issuerPath
            };

            var expectedIssuer = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (string.IsNullOrEmpty(expectedIssuer))
                expectedIssuer = builder.Uri.GetLeftPart(UriPartial.Authority);
            return new Uri(expectedIssuer);
        }

        private static bool AreEquivalentIssuers(Uri expected, Uri actual)
        {
            if (expected == null || actual == null)
                return false;

            var expectedNormalized = NormalizeIssuer(expected);
            var actualNormalized = NormalizeIssuer(actual);
            return string.Equals(expectedNormalized, actualNormalized, StringComparison.Ordinal);
        }

        private static string NormalizeIssuer(Uri issuer)
        {
            var builder = new UriBuilder(issuer.Scheme, issuer.Host, issuer.Port)
            {
                Path = issuer.AbsolutePath?.TrimEnd('/') ?? string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(OAuthClient));
        }
    }
}
