using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    /// <summary>
    /// #745: a token the server rejected can be refreshed before its local expiry (explicit refresh,
    /// <see cref="OAuthClient.ForceRefreshTokenAsync"/>, opt-in 401 retry in
    /// <see cref="AuthInterceptor"/>, RFC 6750 section 3.1), de-duplicated by token rather than by
    /// clock. #746: the refresh presents the stored (possibly rotated) refresh token.
    /// </summary>
    [TestFixture]
    public class OAuthForceRefreshTests
    {
        private const string Key = "provider|client|scope";
        private const string ApiOrigin = "https://api.example.com";

        [Test]
        public void RefreshTokenAsync_UnexpiredStoredToken_StillCallsTokenEndpoint()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));

                var request = NewRefreshRequest();
                request.RefreshToken = "refresh-1";
                request.TokenStoreKey = Key;
                var token = await oauth.RefreshTokenAsync(request, CancellationToken.None);

                Assert.AreEqual(1, idp.Forms.Count);
                Assert.AreEqual("access-1", token.AccessToken);
            });
        }

        [Test]
        public void RefreshTokenAsync_PresentsStoredRotatedRefreshToken()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddMinutes(-5), "refresh-1"));

                // Both callers captured refresh-1 before either refreshed; the first refresh rotates it.
                for (int i = 0; i < 2; i++)
                {
                    var request = NewRefreshRequest();
                    request.RefreshToken = "refresh-1";
                    request.TokenStoreKey = Key;
                    await oauth.RefreshTokenAsync(request, CancellationToken.None);
                }

                Assert.AreEqual(2, idp.Forms.Count);
                StringAssert.Contains("refresh_token=refresh-1", idp.Forms[0]);
                StringAssert.Contains("refresh_token=refresh-2", idp.Forms[1]);
                StringAssert.DoesNotContain("refresh_token=refresh-1", idp.Forms[1]);
            });
        }

        [Test]
        public void ForceRefresh_RejectedTokenStillStored_RefreshesBeforeExpiry()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                var store = new InMemoryTokenStore();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"), store);

                var token = await oauth.ForceRefreshTokenAsync(Key, NewRefreshRequest(), "A", CancellationToken.None);

                Assert.AreEqual(1, idp.Forms.Count);
                Assert.AreEqual("access-1", token.AccessToken);
                Assert.AreEqual("access-1", (await store.GetAsync(Key, CancellationToken.None)).AccessToken);
            });
        }

        [Test]
        public void ForceRefresh_StoredTokenAlreadyReplaced_ReturnsItWithoutTokenRequest()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("B", DateTime.UtcNow.AddHours(1), "refresh-2"));

                var token = await oauth.ForceRefreshTokenAsync(Key, NewRefreshRequest(), "A", CancellationToken.None);

                Assert.AreEqual(0, idp.Forms.Count);
                Assert.AreEqual("B", token.AccessToken);
            });
        }

        [Test]
        public void ForceRefresh_JoinedFlightReturnsPresentedToken_StartsOwnRefresh()
        {
            AssertAsync.Run(async () =>
            {
                // Flight A presents stale "X" while the store already holds "Z": A answers "Z"
                // without a token request. B presents "Z" and joins A's flight while A's store
                // read is held; "Z" is no answer for B, so B starts one refresh of its own.
                var idp = new RotatingTokenEndpoint();
                var store = new GatedTokenStore();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("Z", DateTime.UtcNow.AddHours(1), "refresh-1"), store);

                var first = oauth.ForceRefreshTokenAsync(Key, NewRefreshRequest(), "X", CancellationToken.None);
                await store.FirstGetEntered;
                var second = oauth.ForceRefreshTokenAsync(Key, NewRefreshRequest(), "Z", CancellationToken.None);

                Assert.AreEqual(1, oauth.InFlightRefreshCount, "B joined A's flight instead of starting one");
                Assert.IsFalse(second.IsCompleted);

                store.ReleaseFirstGet();
                var tokenA = await first;
                var tokenB = await second;

                Assert.AreEqual("Z", tokenA.AccessToken);
                Assert.AreEqual("access-1", tokenB.AccessToken);
                Assert.AreEqual(1, idp.Forms.Count);
                StringAssert.Contains("refresh_token=refresh-1", idp.Forms[0]);
                Assert.AreEqual(0, oauth.InFlightRefreshCount);
            });
        }

        [Test]
        public void Interceptor_401FromCrossOriginRedirectHop_IsDeliveredWithoutRefresh()
        {
            AssertAsync.Run(async () =>
            {
                // The auth interceptor wraps the redirect interceptor, so it sees the final 401 of
                // a hop to another origin. That origin never got this interceptor's token: the 401
                // is about its own credentials and is delivered, with no refresh and no resend.
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));
                var hops = new List<string>();
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    lock (hops)
                        hops.Add(req.Uri.GetLeftPart(UriPartial.Path) + " " + (req.Headers.Get("Authorization") ?? "-"));

                    if (req.Uri.Host == "api.example.com")
                    {
                        var redirectHeaders = new HttpHeaders();
                        redirectHeaders.Set("Location", "https://other.example.com/final");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found, redirectHeaders, Array.Empty<byte>(), ctx.Elapsed, req));
                    }

                    var headers = new HttpHeaders();
                    headers.Set("WWW-Authenticate", "Bearer error=\"invalid_token\"");
                    return Task.FromResult(new UHttpResponse(HttpStatusCode.Unauthorized, headers,
                        Encoding.UTF8.GetBytes("denied"), ctx.Elapsed, req));
                });

                var options = new AuthInterceptorOptions { RetryOnUnauthorized = true };
                options.AllowedOrigins.Add(new Uri(ApiOrigin));
                var auth = new AuthInterceptor(new OAuthTokenProvider(oauth, Key, NewRefreshRequest()), options);
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { auth, new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri(ApiOrigin + "/start"));

                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual("denied", response.GetBodyAsString());
                CollectionAssert.AreEqual(
                    new[] { "https://api.example.com/start Bearer A", "https://other.example.com/final -" },
                    hops);
                Assert.AreEqual(0, idp.Forms.Count, "a 401 from another origin refreshed this origin's token");
            });
        }

        [Test]
        public void Interceptor_401_InvalidatesAndRetriesOnceWithNewToken()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));
                var api = new ApiTransport(acceptedAuthorization: "Bearer access-1");
                using var client = CreateApiClient(api, oauth, retryOnUnauthorized: true);

                using var response = await client.Get(ApiOrigin + "/data").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                CollectionAssert.AreEqual(new[] { "Bearer A", "Bearer access-1" }, api.Authorizations);
                Assert.AreEqual(1, idp.Forms.Count);
            });
        }

        [Test]
        public void Interceptor_Second401_IsReturnedAsResponse()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));
                var api = new ApiTransport(acceptedAuthorization: null);
                using var client = CreateApiClient(api, oauth, retryOnUnauthorized: true);

                using var response = await client.Get(ApiOrigin + "/data").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual("denied", response.GetBodyAsString());
                Assert.AreEqual(2, api.Authorizations.Count);
                Assert.AreEqual(1, idp.Forms.Count);
            });
        }

        [Test]
        public void Interceptor_RetryDisabledByDefault()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));
                var api = new ApiTransport(acceptedAuthorization: "Bearer access-1");
                using var client = CreateApiClient(api, oauth, retryOnUnauthorized: false);

                using var response = await client.Get(ApiOrigin + "/data").SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual(1, api.Authorizations.Count);
                Assert.AreEqual(0, idp.Forms.Count);
            });
        }

        [Test]
        public void Interceptor_NonReplayableBody_IsNotRetried()
        {
            AssertAsync.Run(async () =>
            {
                var idp = new RotatingTokenEndpoint();
                using var oauth = await idp.CreateClientAsync(new OAuthToken("A", DateTime.UtcNow.AddHours(1), "refresh-1"));
                var api = new ApiTransport(acceptedAuthorization: "Bearer access-1");
                using var client = CreateApiClient(api, oauth, retryOnUnauthorized: true);

                using var response = await client.Post(ApiOrigin + "/upload")
                    .WithStreamBody(new NonSeekableStream(Encoding.UTF8.GetBytes("payload")))
                    .SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.AreEqual(1, api.Authorizations.Count);
                Assert.AreEqual(0, idp.Forms.Count);
            });
        }

        [Test]
        public void Interceptor_RetryOnUnauthorized_RequiresInvalidator()
        {
            var options = new AuthInterceptorOptions { RetryOnUnauthorized = true };
            Assert.Throws<ArgumentException>(() => new AuthInterceptor(new StaticTokenProvider("t"), options));
        }

        private static UHttpClient CreateApiClient(ApiTransport api, OAuthClient oauth, bool retryOnUnauthorized)
        {
            var options = new AuthInterceptorOptions { RetryOnUnauthorized = retryOnUnauthorized };
            options.AllowedOrigins.Add(new Uri(ApiOrigin));
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new MockTransport(api.Handle),
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor>
                {
                    new AuthInterceptor(new OAuthTokenProvider(oauth, Key, NewRefreshRequest()), options)
                }
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
                    TokenEndpoint = new Uri("https://auth.example.com/token"),
                    RedirectUri = new Uri("myapp://oauth/callback"),
                    Scopes = new[] { "profile" },
                    UsePkce = true
                },
                Scope = "profile"
            };
        }

        // Token endpoint that answers access-N / refresh-(N+1) and records the form bodies.
        private sealed class RotatingTokenEndpoint
        {
            private readonly object _gate = new object();
            private readonly List<string> _forms = new List<string>();

            public IReadOnlyList<string> Forms
            {
                get
                {
                    lock (_gate)
                        return _forms.ToArray();
                }
            }

            public async Task<OAuthClient> CreateClientAsync(OAuthToken stored, ITokenStore store = null)
            {
                store ??= new InMemoryTokenStore();
                await store.SetAsync(Key, stored, CancellationToken.None);
                var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = new MockTransport(Handle),
                    DisposeTransport = true
                });
                // The OAuthClient does not own a client it is given; the tests leak it with the
                // mock transport, which holds no resources.
                return new OAuthClient(client, store);
            }

            private Task<UHttpResponse> Handle(UHttpRequest req, RequestContext ctx, CancellationToken ct)
            {
                int n;
                lock (_gate)
                {
                    _forms.Add(req.GetBodyAsString());
                    n = _forms.Count;
                }

                var headers = new HttpHeaders();
                headers.Set("Content-Type", "application/json");
                return Task.FromResult(new UHttpResponse(HttpStatusCode.OK, headers,
                    Encoding.UTF8.GetBytes("{\"access_token\":\"access-" + n + "\",\"refresh_token\":\"refresh-" + (n + 1) +
                                           "\",\"token_type\":\"Bearer\",\"expires_in\":3600}"),
                    ctx.Elapsed, req));
            }
        }

        // Resource server: 200 for the accepted Authorization value, else 401 with a Bearer challenge.
        private sealed class ApiTransport
        {
            private readonly string _accepted;
            private readonly object _gate = new object();
            private readonly List<string> _authorizations = new List<string>();

            public ApiTransport(string acceptedAuthorization)
            {
                _accepted = acceptedAuthorization;
            }

            public IReadOnlyList<string> Authorizations
            {
                get
                {
                    lock (_gate)
                        return _authorizations.ToArray();
                }
            }

            public Task<UHttpResponse> Handle(UHttpRequest req, RequestContext ctx, CancellationToken ct)
            {
                var authorization = req.Headers.Get("Authorization");
                lock (_gate)
                    _authorizations.Add(authorization);

                if (_accepted != null && authorization == _accepted)
                {
                    return Task.FromResult(new UHttpResponse(HttpStatusCode.OK, new HttpHeaders(),
                        Encoding.UTF8.GetBytes("ok"), ctx.Elapsed, req));
                }

                var headers = new HttpHeaders();
                headers.Set("WWW-Authenticate", "Bearer error=\"invalid_token\"");
                return Task.FromResult(new UHttpResponse(HttpStatusCode.Unauthorized, headers,
                    Encoding.UTF8.GetBytes("denied"), ctx.Elapsed, req));
            }
        }

        // Token store whose first read waits until the test releases it.
        private sealed class GatedTokenStore : ITokenStore
        {
            private readonly InMemoryTokenStore _inner = new InMemoryTokenStore();
            private readonly TaskCompletionSource<bool> _entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> _release =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int _reads;

            public Task FirstGetEntered => _entered.Task;

            public void ReleaseFirstGet()
            {
                _release.TrySetResult(true);
            }

            public async Task<OAuthToken> GetAsync(string key, CancellationToken ct)
            {
                if (Interlocked.Increment(ref _reads) == 1)
                {
                    _entered.TrySetResult(true);
                    await _release.Task.ConfigureAwait(false);
                }

                return await _inner.GetAsync(key, ct).ConfigureAwait(false);
            }

            public Task SetAsync(string key, OAuthToken token, CancellationToken ct)
            {
                return _inner.SetAsync(key, token, ct);
            }

            public Task RemoveAsync(string key, CancellationToken ct)
            {
                return _inner.RemoveAsync(key, ct);
            }
        }

        private sealed class NonSeekableStream : MemoryStream
        {
            public NonSeekableStream(byte[] data) : base(data)
            {
            }

            public override bool CanSeek => false;
        }
    }
}
