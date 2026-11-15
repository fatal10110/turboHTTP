using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using UnityEngine;

namespace TurboHTTP.Samples.Authentication
{
    /// <summary>
    /// Example of token-based authentication using AuthInterceptor.
    /// The interceptor attaches "Authorization: Bearer <token>" to every request this client sends, whatever
    /// the host, except redirect hops to another origin. Use a dedicated client per authenticated origin, or
    /// restrict the token with AuthInterceptorOptions.AllowedOrigins.
    /// Demonstrates token injection and refresh logic. The token is a secret: this sample never logs it
    /// or a response body that echoes it. For a real OAuth flow use OAuthTokenProvider (TurboHTTP.Auth),
    /// which gates concurrent refreshes and refreshes before expiry.
    /// </summary>
    public class AuthenticationExample : MonoBehaviour
    {
        private UHttpClient _client;
        private DynamicTokenProvider _tokenProvider;
        private CancellationTokenSource _cts;

        void Start()
        {
            // Create a token provider that can fetch/refresh tokens
            _tokenProvider = new DynamicTokenProvider(FetchTokenAsync);

            var options = new UHttpClientOptions
            {
                BaseUrl = "https://httpbin.org" // Using httpbin to test headers
            };

            // Add the auth interceptor
            options.Interceptors.Add(new AuthInterceptor(_tokenProvider));

            _client = new UHttpClient(options);
            _cts = new CancellationTokenSource();

            RunExamples();
        }

        // async void is only the Unity entry point: everything that can throw is caught here.
        async void RunExamples()
        {
            try
            {
                await Example1_AuthenticatedRequest();
                await Example2_TokenRefresh();
            }
            catch (UHttpException ex)
            {
                Debug.LogError($"Request failed: {ex.HttpError.Type} - {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                // Destroyed while a request was in flight.
            }
            finally
            {
                // The entry point owns disposal, so no continuation can touch a disposed client.
                _cts?.Dispose();
                _cts = null;
                _client?.Dispose();
                _client = null;
            }
        }

        async Task Example1_AuthenticatedRequest()
        {
            Debug.Log("=== Example 1: Authenticated Request ===");

            // The interceptor will automatically add the Authorization header
            using var response = await _client.Get("/bearer").SendBufferedAsync(_cts.Token);

            // httpbin/bearer answers 200 when it received a bearer token. Its body echoes the token,
            // so it is not logged.
            Debug.Log($"Status: {response.StatusCode}");
        }

        async Task Example2_TokenRefresh()
        {
            Debug.Log("=== Example 2: Token Refresh ===");

            // Simulate token expiration
            Debug.Log("Simulating token expiration...");
            _tokenProvider.InvalidateToken();

            // Next request should trigger a refresh
            using var response = await _client.Get("/bearer").SendBufferedAsync(_cts.Token);

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log("Token was refreshed automatically!");
        }

        // Mock token fetching logic
        private async Task<string> FetchTokenAsync(CancellationToken cancellationToken)
        {
            Debug.Log("Fetching new access token...");
            await Task.Delay(500, cancellationToken); // Simulate network delay
            return "access-token-" + Guid.NewGuid().ToString().Substring(0, 8);
        }

        void OnDestroy()
        {
            _cts?.Cancel();
        }
    }

    /// <summary>
    /// Token provider that calls a delegate to get a token. AuthInterceptor calls GetTokenAsync from
    /// thread-pool threads, so the cached token is read with Volatile and a refresh is serialized by a
    /// SemaphoreSlim: concurrent requests share one fetch instead of each starting their own.
    /// The caller's CancellationToken reaches the fetch delegate.
    /// </summary>
    public class DynamicTokenProvider : IAuthTokenProvider
    {
        private readonly Func<CancellationToken, Task<string>> _fetchTokenFunc;
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private string _cachedToken;

        public DynamicTokenProvider(Func<CancellationToken, Task<string>> fetchTokenFunc)
        {
            _fetchTokenFunc = fetchTokenFunc ?? throw new ArgumentNullException(nameof(fetchTokenFunc));
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            var token = Volatile.Read(ref _cachedToken);
            if (!string.IsNullOrEmpty(token))
                return token;

            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Another caller may have refreshed while this one waited for the gate.
                token = Volatile.Read(ref _cachedToken);
                if (string.IsNullOrEmpty(token))
                {
                    token = await _fetchTokenFunc(cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _cachedToken, token);
                }
                return token;
            }
            finally
            {
                _refreshGate.Release();
            }
        }

        public void InvalidateToken()
        {
            Volatile.Write(ref _cachedToken, null);
        }
    }
}
