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
    /// The interceptor attaches "Authorization: Bearer <token>" and keeps the token bound to the origin it
    /// was first used with, so it is never sent to another host (for example after a cross-origin redirect).
    /// Demonstrates token injection and refresh logic.
    /// </summary>
    public class AuthenticationExample : MonoBehaviour
    {
        private UHttpClient _client;
        private DynamicTokenProvider _tokenProvider;

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

            RunExamples();
        }

        async void RunExamples()
        {
            await Example1_AuthenticatedRequest();
            await Example2_TokenRefresh();
        }

        async Task Example1_AuthenticatedRequest()
        {
            Debug.Log("=== Example 1: Authenticated Request ===");

            // The interceptor will automatically add the Authorization header
            using var response = await _client.Get("/bearer").SendBufferedAsync();

            Debug.Log($"Status: {response.StatusCode}");
            // httpbin/bearer returns the token it received
            Debug.Log($"Body: {response.GetBodyAsString()}");
        }

        async Task Example2_TokenRefresh()
        {
            Debug.Log("=== Example 2: Token Refresh ===");

            // Simulate token expiration
            Debug.Log("Simulating token expiration...");
            _tokenProvider.InvalidateToken();

            // Next request should trigger a refresh
            using var response = await _client.Get("/bearer").SendBufferedAsync();

            Debug.Log($"Status: {response.StatusCode}");
            Debug.Log("Token was refreshed automatically!");
        }

        // Mock token fetching logic
        private async Task<string> FetchTokenAsync()
        {
            Debug.Log("Fetching new access token...");
            await Task.Delay(500); // Simulate network delay
            return "access-token-" + Guid.NewGuid().ToString().Substring(0, 8);
        }

        void OnDestroy()
        {
            _client?.Dispose();
        }
    }

    /// <summary>
    /// Simple token provider that calls a delegate to get a token.
    /// </summary>
    public class DynamicTokenProvider : IAuthTokenProvider
    {
        private readonly Func<Task<string>> _fetchTokenFunc;
        private string _cachedToken;

        public DynamicTokenProvider(Func<Task<string>> fetchTokenFunc)
        {
            _fetchTokenFunc = fetchTokenFunc;
        }

        public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(_cachedToken))
            {
                _cachedToken = await _fetchTokenFunc();
            }
            return _cachedToken;
        }

        public void InvalidateToken()
        {
            _cachedToken = null;
        }
    }
}
