using System;
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
    [TestFixture]
    public class OAuthClientTests
    {
        [Test]
        public void Pkce_GeneratesValidChallenge()
        {
            const string verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
            const string expectedChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

            var challenge = PkceUtility.CreateS256CodeChallenge(verifier);

            Assert.AreEqual(expectedChallenge, challenge);
            Assert.IsTrue(PkceUtility.ValidateCodeVerifier(verifier));
        }

        [Test]
        public void Pkce_GeneratedVerifier_IsValidAcrossBounds()
        {
            var min = PkceUtility.GenerateCodeVerifier(43);
            var max = PkceUtility.GenerateCodeVerifier(128);

            Assert.AreEqual(43, min.Length);
            Assert.AreEqual(128, max.Length);
            Assert.IsTrue(PkceUtility.ValidateCodeVerifier(min));
            Assert.IsTrue(PkceUtility.ValidateCodeVerifier(max));
        }

        [Test]
        public void AuthCodeExchange_ParsesToken()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((request, context, ct) =>
                {
                    StringAssert.Contains("grant_type=authorization_code", request.GetBodyAsString());
                    var body = Encoding.UTF8.GetBytes("{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"token_type\":\"Bearer\",\"expires_in\":120,\"scope\":\"openid profile\",\"id_token\":\"id-1\"}");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        body,
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });
                using var oauth = new OAuthClient(client);

                var config = CreateConfig();
                var token = await oauth.ExchangeCodeAsync(new OAuthCodeExchangeRequest
                {
                    Config = config,
                    AuthorizationCode = "code-123",
                    CodeVerifier = PkceUtility.GenerateCodeVerifier()
                }, CancellationToken.None);

                Assert.AreEqual("access-1", token.AccessToken);
                Assert.AreEqual("refresh-1", token.RefreshToken);
                Assert.AreEqual("Bearer", token.TokenType);
                Assert.AreEqual("id-1", token.IdToken);
                Assert.Greater(token.ExpiresAtUtc, DateTime.UtcNow);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Refresh_SingleFlight()
        {
            Task.Run(async () =>
            {
                var refreshCalls = 0;
                var transport = new MockTransport(async (request, context, ct) =>
                {
                    Interlocked.Increment(ref refreshCalls);
                    await Task.Delay(40, ct);
                    var body = Encoding.UTF8.GetBytes("{\"access_token\":\"fresh\",\"refresh_token\":\"refresh-1\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
                    return new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        body,
                        context.Elapsed,
                        request);
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var store = new InMemoryTokenStore();
                var key = "provider|client|scope";
                await store.SetAsync(
                    key,
                    new OAuthToken(
                        accessToken: "stale",
                        expiresAtUtc: DateTime.UtcNow.AddMinutes(-10),
                        refreshToken: "refresh-1"),
                    CancellationToken.None);

                using var oauth = new OAuthClient(client, store);
                var refresh = new OAuthRefreshRequest
                {
                    Config = CreateConfig(),
                    Scope = "openid profile"
                };

                var t1 = oauth.GetValidTokenAsync(key, refresh, CancellationToken.None);
                var t2 = oauth.GetValidTokenAsync(key, refresh, CancellationToken.None);
                var t3 = oauth.GetValidTokenAsync(key, refresh, CancellationToken.None);
                var tokens = await Task.WhenAll(t1, t2, t3);

                Assert.AreEqual(1, refreshCalls);
                Assert.AreEqual("fresh", tokens[0].AccessToken);
                Assert.AreEqual("fresh", tokens[1].AccessToken);
                Assert.AreEqual("fresh", tokens[2].AccessToken);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Refresh_InvalidGrant_ClearsToken()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((request, context, ct) =>
                {
                    var body = Encoding.UTF8.GetBytes("{\"error\":\"invalid_grant\"}");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.BadRequest,
                        new HttpHeaders(),
                        body,
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var store = new InMemoryTokenStore();
                var key = "provider|client|scope";
                await store.SetAsync(
                    key,
                    new OAuthToken(
                        accessToken: "stale",
                        expiresAtUtc: DateTime.UtcNow.AddMinutes(-20),
                        refreshToken: "refresh-1"),
                    CancellationToken.None);

                using var oauth = new OAuthClient(client, store);
                var refresh = new OAuthRefreshRequest
                {
                    Config = CreateConfig(),
                    Scope = "openid profile"
                };

                await TestHelpers.AssertThrowsAsync<InvalidOperationException>(async () =>
                {
                    await oauth.GetValidTokenAsync(key, refresh, CancellationToken.None);
                });

                var stored = await store.GetAsync(key, CancellationToken.None);
                Assert.IsNull(stored);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void StateMismatch_Fails()
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                OAuthClient.ValidateState("expected", "actual");
            });
        }

        [Test]
        public void Discovery_OverridesEndpoints()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((request, context, ct) =>
                {
                    var body = Encoding.UTF8.GetBytes("{\"authorization_endpoint\":\"https://id.example.com/auth\",\"token_endpoint\":\"https://id.example.com/token\",\"issuer\":\"https://id.example.com\"}");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        body,
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });
                using var oauth = new OAuthClient(client);

                var config = CreateConfig();
                config.UseOidcDiscovery = true;
                var originalAuth = config.AuthorizationEndpoint;
                var originalToken = config.TokenEndpoint;

                var resolved = await oauth.ResolveEndpointsAsync(
                    config,
                    new Uri("https://id.example.com/.well-known/openid-configuration"),
                    CancellationToken.None);

                Assert.AreNotSame(config, resolved);
                Assert.AreEqual("https://id.example.com/auth", resolved.AuthorizationEndpoint.ToString());
                Assert.AreEqual("https://id.example.com/token", resolved.TokenEndpoint.ToString());
                Assert.AreEqual(originalAuth, config.AuthorizationEndpoint);
                Assert.AreEqual(originalToken, config.TokenEndpoint);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void SensitiveData_NotLogged()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((request, context, ct) =>
                {
                    var body = Encoding.UTF8.GetBytes("{\"access_token\":\"secret-token\"");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        body,
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });
                using var oauth = new OAuthClient(client);

                var ex = await TestHelpers.AssertThrowsAsync<InvalidOperationException>(async () =>
                {
                    await oauth.ExchangeCodeAsync(new OAuthCodeExchangeRequest
                    {
                        Config = CreateConfig(),
                        AuthorizationCode = "code",
                        CodeVerifier = PkceUtility.GenerateCodeVerifier()
                    }, CancellationToken.None);
                });

                StringAssert.DoesNotContain("secret-token", ex.Message);
            }).GetAwaiter().GetResult();
        }

        private static OAuthConfig CreateConfig()
        {
            return new OAuthConfig
            {
                ClientId = "client-1",
                AuthorizationEndpoint = new Uri("https://auth.example.com/authorize"),
                TokenEndpoint = new Uri("https://auth.example.com/token"),
                RedirectUri = new Uri("myapp://oauth/callback"),
                Scopes = new[] { "openid", "profile" },
                UsePkce = true
            };
        }
    }

    internal static class OAuthTestRequestExtensions
    {
        public static string GetBodyAsString(this UHttpRequest request)
        {
            if (request?.Body == null)
                return string.Empty;
            return Encoding.UTF8.GetString(request.Body);
        }
    }
}
