using System;
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
    /// <summary>
    /// A client for performing OAuth 2.0 authorization, token exchange, and refresh operations.
    /// </summary>
    public sealed class OAuthClient : IDisposable
    {
        private readonly UHttpClient _client;
        private readonly bool _ownsClient;
        private readonly ITokenStore _tokenStore;
        private readonly Func<DateTime> _utcNow;
        private readonly TimeSpan _defaultRefreshSkew;

        // One in-flight refresh per token-store key (#744). _refreshLock owns _refreshFlights: the
        // caller that finds no entry adds one and starts RunRefreshFlightAsync, which removes the entry
        // before it completes the shared task, so a key holds state only while its refresh runs (#743).
        // Every waiter awaits the same task with its own token, so one caller's cancellation never
        // cancels the refresh for the others; the refresh itself runs on _lifetimeCts (Dispose).
        private readonly object _refreshLock = new object();
        private readonly Dictionary<string, Task<OAuthToken>> _refreshFlights
            = new Dictionary<string, Task<OAuthToken>>(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();
        private int _disposed;

        /// <summary> Initializes a new instance of <see cref="OAuthClient"/>. </summary>
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

        /// <summary> Creates an authorization request. </summary>
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
                // RFC 6749 3.1.2.3 / 4.1.3: the registered string, not Uri.ToString() (which adds a
                // trailing "/" to authority-only URIs and unescapes) (#741).
                ["redirect_uri"] = config.RedirectUri.OriginalString,
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

        /// <summary> Exchanges an authorization code for an OAuth token. </summary>
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
                ["redirect_uri"] = request.RedirectUri ?? request.Config.RedirectUri.OriginalString
            };

            if (!string.IsNullOrEmpty(request.CodeVerifier))
                form["code_verifier"] = request.CodeVerifier;

            using var response = await SendTokenRequestAsync(request.Config.TokenEndpoint, form, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var token = ParseTokenResponse(response.GetBodyAsString());
            ValidateOidcTokenPresence(request.Config, token);
            return token;
        }

        /// <summary>
        /// Refreshes the token stored under the request's key (or the key built from its config and
        /// scope) through the token endpoint, whether or not the stored token has expired locally.
        /// </summary>
        /// <remarks>
        /// Concurrent refreshes for one key share a single token request: every caller receives its
        /// token or its failure, and a failed refresh is not repeated by the waiters (#744). The
        /// refresh token sent is the stored one when the store holds a token, so a token rotated by
        /// an earlier refresh is never presented again; <see cref="OAuthRefreshRequest.RefreshToken"/>
        /// is used only while the store is empty (#746). Cancelling <paramref name="ct"/> stops this
        /// caller's wait; the shared refresh continues for the other callers.
        /// </remarks>
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

            return await RefreshSharedAsync(
                    key, request.Config, request.Scope, request.RefreshToken, presentedAccessToken: null, ct)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Refreshes the token stored under <paramref name="key"/> through the token endpoint after a
        /// resource server rejected <paramref name="rejectedAccessToken"/> (RFC 6750 section 3.1),
        /// regardless of the token's local expiry time.
        /// </summary>
        /// <param name="key">Token-store key of the token to replace.</param>
        /// <param name="refreshRequest">Config and scope for the refresh; its
        /// <see cref="OAuthRefreshRequest.RefreshToken"/> is used only when the store holds no token,
        /// and its <see cref="OAuthRefreshRequest.TokenStoreKey"/> is ignored. The object is not
        /// modified.</param>
        /// <param name="rejectedAccessToken">The access token the server rejected. When the stored
        /// token's access token already differs from it, another caller has replaced it and the
        /// stored token is returned without a token request. When the store holds no token, the
        /// method returns <c>null</c>. Pass <c>null</c> to refresh unconditionally.</param>
        /// <param name="ct">Cancels this caller's wait; a refresh shared with other callers continues.</param>
        /// <returns>The replacement token, or <c>null</c> when the store holds no token for
        /// <paramref name="key"/> and <paramref name="rejectedAccessToken"/> is set.</returns>
        /// <exception cref="InvalidOperationException">No refresh token is available, or the token
        /// endpoint rejected it (<c>invalid_grant</c>, after which the stored token is removed).
        /// Reauthorization is required.</exception>
        public async Task<OAuthToken> ForceRefreshTokenAsync(
            string key,
            OAuthRefreshRequest refreshRequest,
            string rejectedAccessToken,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Token store key is required.", nameof(key));
            if (refreshRequest == null) throw new ArgumentNullException(nameof(refreshRequest));
            if (refreshRequest.Config == null)
                throw new ArgumentException("Config is required.", nameof(refreshRequest));

            refreshRequest.Config.Validate();

            return await RefreshSharedAsync(
                    key,
                    refreshRequest.Config,
                    refreshRequest.Scope,
                    refreshRequest.RefreshToken,
                    string.IsNullOrEmpty(rejectedAccessToken) ? null : rejectedAccessToken,
                    ct)
                .ConfigureAwait(false);
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

            using var response = await _client.Get(discoveryEndpoint.OriginalString)
                .WithMetadata(AuthInterceptor.SkipAuthMetadataKey, true)
                .SendBufferedAsync(ct)
                .ConfigureAwait(false);
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

            if (refreshRequest.Config == null)
                throw new ArgumentException("Config is required.", nameof(refreshRequest));
            refreshRequest.Config.Validate();

            // Present the expired access token: when another caller has already replaced it, the
            // refresh returns the stored token without a token request (#745).
            return await RefreshSharedAsync(
                    key, refreshRequest.Config, refreshRequest.Scope, token.RefreshToken, token.AccessToken, ct)
                .ConfigureAwait(false);
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

            // Stops in-flight refreshes; their waiters observe the cancellation. The source is not
            // disposed: a refresh that is still unwinding may read its token.
            _lifetimeCts.Cancel();

            if (_ownsClient)
                _client.Dispose();
        }

        /// <summary>Number of keys with a refresh in flight (tests, #743).</summary>
        internal int InFlightRefreshCount
        {
            get
            {
                lock (_refreshLock)
                    return _refreshFlights.Count;
            }
        }

        private async Task<OAuthToken> RefreshSharedAsync(
            string key,
            OAuthConfig config,
            string scope,
            string fallbackRefreshToken,
            string presentedAccessToken,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // A joined refresh that returns the very token this caller presented (a refresh that
            // started from an older token and found the presented one in the store) is not an
            // answer for this caller: start one refresh of its own. Bounded to one extra round.
            for (var round = 0; ; round++)
            {
                TaskCompletionSource<OAuthToken> started = null;
                Task<OAuthToken> flight;
                lock (_refreshLock)
                {
                    ThrowIfDisposed();
                    if (!_refreshFlights.TryGetValue(key, out flight))
                    {
                        started = new TaskCompletionSource<OAuthToken>(TaskCreationOptions.RunContinuationsAsynchronously);
                        flight = started.Task;
                        _refreshFlights.Add(key, flight);
                    }
                }

                if (started != null)
                {
                    // Owned by `started`: RunRefreshFlightAsync never throws, it completes `started`
                    // on every path and removes the map entry first.
                    _ = RunRefreshFlightAsync(key, config, scope, fallbackRefreshToken, presentedAccessToken, started);
                }

                var token = await AuthTaskWait.WaitAsync(flight, ct).ConfigureAwait(false);
                if (started != null || round > 0 || presentedAccessToken == null || token == null ||
                    !string.Equals(token.AccessToken, presentedAccessToken, StringComparison.Ordinal))
                {
                    return token;
                }
            }
        }

        private async Task RunRefreshFlightAsync(
            string key,
            OAuthConfig config,
            string scope,
            string fallbackRefreshToken,
            string presentedAccessToken,
            TaskCompletionSource<OAuthToken> flight)
        {
            OAuthToken token = null;
            Exception failure = null;
            try
            {
                token = await RefreshCoreAsync(
                        key, config, scope, fallbackRefreshToken, presentedAccessToken, _lifetimeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            // Remove before completing so a caller that sees the result and asks again starts anew.
            lock (_refreshLock)
            {
                if (_refreshFlights.TryGetValue(key, out var current) && ReferenceEquals(current, flight.Task))
                    _refreshFlights.Remove(key);
            }

            if (failure == null)
            {
                flight.TrySetResult(token);
            }
            else if (failure is OperationCanceledException)
            {
                flight.TrySetCanceled();
            }
            else
            {
                flight.TrySetException(failure);
                // Mark the failure observed: every waiter may have stopped waiting already.
                _ = flight.Task.Exception;
            }
        }

        private async Task<OAuthToken> RefreshCoreAsync(
            string key,
            OAuthConfig config,
            string scope,
            string fallbackRefreshToken,
            string presentedAccessToken,
            CancellationToken ct)
        {
            ThrowIfDisposed();
            var existing = await _tokenStore.GetAsync(key, ct).ConfigureAwait(false);

            // De-duplicate by token, not by clock (#745): the caller's token was already replaced.
            if (presentedAccessToken != null)
            {
                if (existing == null)
                    return null;
                if (!string.Equals(existing.AccessToken, presentedAccessToken, StringComparison.Ordinal))
                    return existing;
            }

            // The stored refresh token wins over the one the caller captured earlier, which an
            // earlier rotation may have retired (RFC 6749 section 6, #746).
            var refreshToken = existing != null && !string.IsNullOrWhiteSpace(existing.RefreshToken)
                ? existing.RefreshToken
                : fallbackRefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException("No refresh token is available. Reauthorization is required.");

            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = config.ClientId
            };

            if (!string.IsNullOrWhiteSpace(scope))
                form["scope"] = scope;

            using var response = await SendTokenRequestAsync(config.TokenEndpoint, form, ct)
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

            // RFC 6749 section 6: the server MAY issue a new refresh token; when it does not,
            // the client keeps using the one it just presented.
            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                token = new OAuthToken(
                    token.AccessToken,
                    token.ExpiresAtUtc,
                    refreshToken,
                    token.TokenType,
                    token.IdToken,
                    token.Scope);
            }

            await _tokenStore.SetAsync(key, token, ct).ConfigureAwait(false);
            return token;
        }

        private async ValueTask<UHttpResponse> SendTokenRequestAsync(
            Uri endpoint,
            IDictionary<string, string> formFields,
            CancellationToken ct)
        {
            var formBody = BuildFormBody(formFields);
            var response = await _client
                .Post(endpoint.OriginalString)
                .WithHeader("Content-Type", "application/x-www-form-urlencoded")
                .WithBody(formBody)
                // Never attach (or fetch) the access token on token-endpoint requests: an OAuth
                // AuthInterceptor on _client would re-enter the refresh gate held by the caller.
                .WithMetadata(AuthInterceptor.SkipAuthMetadataKey, true)
                .SendBufferedAsync(ct)
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
            // Start from the configured string: GetLeftPart/Query/ToString re-encode or unescape it
            // (#741). RFC 6749 section 3.1 keeps the endpoint's own query; a fragment is not allowed.
            var endpoint = baseUri.OriginalString.Trim();
            var fragment = endpoint.IndexOf('#');
            if (fragment >= 0)
                endpoint = endpoint.Substring(0, fragment);

            var sb = new StringBuilder(endpoint);
            var queryStart = endpoint.IndexOf('?');
            if (queryStart < 0)
                sb.Append('?');
            else if (queryStart < endpoint.Length - 1 && endpoint[endpoint.Length - 1] != '&')
                sb.Append('&');

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
            // The document model is always LiteJson, so a custom JsonSerializer.Default cannot turn the
            // token response into a tree this code does not read (#1034).
            var result = ProjectJsonBridge.DeserializeDom(json, requiredBy: "OAuth token parsing");
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
