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
    /// #744: concurrent refreshes for one key share one token request, its failure included, and
    /// the AuthInterceptor's wait for the provider is bounded by the request timeout. #743: no
    /// per-key refresh state outlives the refresh.
    /// </summary>
    [TestFixture]
    public class OAuthRefreshSharedFailureTests
    {
        private const string Key = "provider|client|scope";

        [Test]
        public void FailedRefresh_IsSharedByWaiters_AndNotRepeated()
        {
            AssertAsync.Run(async () =>
            {
                var calls = 0;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult(true);
                    await gate.Task.ConfigureAwait(false);
                    return new UHttpResponse(HttpStatusCode.ServiceUnavailable, new HttpHeaders(),
                        Encoding.UTF8.GetBytes("{\"error\":\"temporarily_unavailable\"}"), ctx.Elapsed, req);
                });

                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", DateTime.UtcNow.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store);

                var first = oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);
                await entered.Task.ConfigureAwait(false);

                var waiters = new List<Task<OAuthToken>> { first };
                for (int i = 0; i < 3; i++)
                    waiters.Add(oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None));
                for (int i = 1; i < waiters.Count; i++)
                    Assert.IsFalse(waiters[i].IsCompleted, "waiter " + i + " must wait for the shared refresh");

                gate.SetResult(true);

                for (int i = 0; i < waiters.Count; i++)
                {
                    Exception failure = null;
                    try
                    {
                        await waiters[i].ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }

                    Assert.IsNotNull(failure, "waiter " + i + " must see the refresh failure");
                }

                Assert.AreEqual(1, Volatile.Read(ref calls), "waiters must not repeat a failed refresh");
                Assert.AreEqual(0, oauth.InFlightRefreshCount);
            });
        }

        [Test]
        public void CancelledStarter_DoesNotCancelSharedRefresh()
        {
            AssertAsync.Run(async () =>
            {
                var calls = 0;
                var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    Interlocked.Increment(ref calls);
                    entered.TrySetResult(true);
                    using (ct.Register(() => gate.TrySetCanceled()))
                        await gate.Task.ConfigureAwait(false);

                    var headers = new HttpHeaders();
                    headers.Set("Content-Type", "application/json");
                    return new UHttpResponse(HttpStatusCode.OK, headers,
                        Encoding.UTF8.GetBytes("{\"access_token\":\"fresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}"),
                        ctx.Elapsed, req);
                });

                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                var store = new InMemoryTokenStore();
                await store.SetAsync(Key, new OAuthToken("stale", DateTime.UtcNow.AddMinutes(-10), "refresh-1"), CancellationToken.None);
                using var oauth = new OAuthClient(client, store);

                using var starterCts = new CancellationTokenSource();
                var starter = oauth.GetValidTokenAsync(Key, NewRefreshRequest(), starterCts.Token);
                await entered.Task.ConfigureAwait(false);
                var waiter = oauth.GetValidTokenAsync(Key, NewRefreshRequest(), CancellationToken.None);

                starterCts.Cancel();
                await TestHelpers.AssertThrowsAsync<OperationCanceledException>(async () => await starter);
                Assert.IsFalse(waiter.IsCompleted, "the shared refresh must survive the starter's cancellation");

                gate.TrySetResult(true);
                var token = await waiter.ConfigureAwait(false);

                Assert.AreEqual("fresh", token.AccessToken);
                Assert.AreEqual(1, Volatile.Read(ref calls));
            });
        }

        [Test]
        public void RefreshState_IsNotRetainedPerKey()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Content-Type", "application/json");
                    return Task.FromResult(new UHttpResponse(HttpStatusCode.OK, headers,
                        Encoding.UTF8.GetBytes("{\"access_token\":\"fresh\",\"token_type\":\"Bearer\",\"expires_in\":3600}"),
                        ctx.Elapsed, req));
                });

                using var client = new UHttpClient(new UHttpClientOptions { Transport = transport, DisposeTransport = true });
                using var oauth = new OAuthClient(client, new InMemoryTokenStore());

                for (int i = 0; i < 8; i++)
                {
                    var request = NewRefreshRequest();
                    request.RefreshToken = "refresh-" + i;
                    request.TokenStoreKey = "key-" + i;
                    await oauth.RefreshTokenAsync(request, CancellationToken.None);
                }

                Assert.AreEqual(0, oauth.InFlightRefreshCount);
            });
        }

        [Test]
        public void AuthInterceptor_HungProvider_FailsWithTimeout()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    Interceptors = new List<IHttpInterceptor> { new AuthInterceptor(new NeverCompletingProvider()) }
                });

                var error = await TestHelpers.AssertThrowsAsync<UHttpException>(async () =>
                {
                    using var response = await client.Get("https://api.example.com/data")
                        .WithTimeout(TimeSpan.FromMilliseconds(200))
                        .SendBufferedAsync();
                });

                Assert.AreEqual(UHttpErrorType.Timeout, error.HttpError.Type);
                Assert.AreEqual(0, transport.RequestCount, "the request must not be dispatched");
            }, TimeSpan.FromSeconds(60));
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

        // Ignores cancellation and never answers: only the interceptor's bound can end the wait.
        private sealed class NeverCompletingProvider : IAuthTokenProvider
        {
            private readonly TaskCompletionSource<string> _never = new TaskCompletionSource<string>();

            public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => _never.Task;
        }
    }
}
