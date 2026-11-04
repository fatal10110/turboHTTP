using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    /// <summary>
    /// Regression tests for #53 (refresh keeps the previous refresh_token when the server does not
    /// rotate it, RFC 6749 section 6) and #69 (refreshing through a client that carries the OAuth
    /// AuthInterceptor must not deadlock or send the access token to the token endpoint).
    /// </summary>
    [TestFixture]
    public class OAuthRefreshTokenTests
    {
        private const string Key = "provider|client|scope";
        private const string TokenEndpoint = "https://auth.example.com/token";

        [Test]
        public void Refresh_WithoutRefreshTokenInResponse_KeepsPreviousRefreshToken()
        {
            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var formBodies = new List<string>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    formBodies.Add(req.GetBodyAsString());
                    return Task.FromResult(TokenResponse(req, ctx,
                        "{\"access_token\":\"access-" + formBodies.Count + "\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));
                });

                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", clock.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store, () => clock);

                var first = await oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);
                Assert.AreEqual("access-1", first.AccessToken);
                Assert.AreEqual("refresh-1", first.RefreshToken);
                Assert.AreEqual("refresh-1", (await store.GetAsync(Key, CancellationToken.None)).RefreshToken);

                // Second expiration: must refresh again with the carried-over refresh token.
                clock = clock.AddHours(2);
                var second = await oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);

                Assert.AreEqual(2, formBodies.Count);
                Assert.AreEqual("access-2", second.AccessToken);
                Assert.AreEqual("refresh-1", second.RefreshToken);
                StringAssert.Contains("refresh_token=refresh-1", formBodies[1]);
            });
        }

        [Test]
        public void Refresh_WithRotatedRefreshToken_ReplacesPrevious()
        {
            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var formBodies = new List<string>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    formBodies.Add(req.GetBodyAsString());
                    var n = formBodies.Count;
                    return Task.FromResult(TokenResponse(req, ctx,
                        "{\"access_token\":\"access-" + n + "\",\"refresh_token\":\"refresh-" + (n + 1) +
                        "\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));
                });

                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", clock.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store, () => clock);

                var first = await oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);
                Assert.AreEqual("refresh-2", first.RefreshToken);

                clock = clock.AddHours(2);
                var second = await oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);

                Assert.AreEqual("access-2", second.AccessToken);
                Assert.AreEqual("refresh-3", second.RefreshToken);
                StringAssert.Contains("refresh_token=refresh-2", formBodies[1]);
            });
        }

        [Test]
        public void Refresh_ThroughClientCarryingOAuthAuthInterceptor_CompletesWithoutLeakingToken()
        {
            AssertAsync.Run(async () =>
            {
                var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                string tokenRequestAuthorization = "<not sent>";
                string apiRequestAuthorization = null;
                var tokenRequests = 0;

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsoluteUri == TokenEndpoint)
                    {
                        Interlocked.Increment(ref tokenRequests);
                        tokenRequestAuthorization = req.Headers.Get("Authorization");
                        return Task.FromResult(TokenResponse(req, ctx,
                            "{\"access_token\":\"fresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));
                    }

                    apiRequestAuthorization = req.Headers.Get("Authorization");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
                });

                var lazyProvider = new LazyTokenProvider();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = new List<IHttpInterceptor> { new AuthInterceptor(lazyProvider) }
                });

                var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", clock.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store, () => clock);
                lazyProvider.Inner = new OAuthTokenProvider(oauth, Key, NewRefreshRequest());

                // Bound: without the fix the token request re-enters the per-key refresh gate and
                // waits forever; the cancellation turns that hang into a failure.
                using var cts = new CancellationTokenSource();
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var response = await client.Get("https://api.example.com/data").SendBufferedAsync(cts.Token);

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, tokenRequests);
                Assert.IsNull(tokenRequestAuthorization, "the token endpoint request must not carry the API bearer token");
                Assert.AreEqual("Bearer fresh", apiRequestAuthorization);
            });
        }

        private static OAuthRefreshRequest NewRefreshRequest()
        {
            return new OAuthRefreshRequest
            {
                Config = new OAuthConfig
                {
                    ClientId = "client-1",
                    AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
                    TokenEndpoint = new Uri(TokenEndpoint),
                    RedirectUri = new Uri("myapp://oauth/callback"),
                    Scopes = new[] { "openid", "profile" },
                    UsePkce = true
                },
                Scope = "openid profile"
            };
        }

        private static UHttpResponse TokenResponse(UHttpRequest request, RequestContext context, string json)
        {
            var headers = new HttpHeaders();
            headers.Set("Content-Type", "application/json");
            return new UHttpResponse(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes(json), context.Elapsed, request);
        }

        private sealed class LazyTokenProvider : IAuthTokenProvider
        {
            public IAuthTokenProvider Inner;

            public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            {
                return Inner.GetTokenAsync(cancellationToken);
            }
        }
    }
}
