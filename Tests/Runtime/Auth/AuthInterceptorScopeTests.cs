using System;
using System.Collections.Generic;
using System.Net;
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
    /// Regression tests for #740 and #618: the token goes only to the configured origins, and an
    /// Authorization header the caller set on the request is not overwritten unless asked.
    /// </summary>
    [TestFixture]
    public class AuthInterceptorScopeTests
    {
        private sealed class CountingTokenProvider : IAuthTokenProvider
        {
            private int _calls;

            public int Calls => Volatile.Read(ref _calls);

            public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _calls);
                return Task.FromResult("secret-token");
            }
        }

        private sealed class Hop
        {
            public string Uri;
            public string Authorization;
        }

        private static AuthInterceptor CreateScoped(IAuthTokenProvider provider, bool overrideExisting, params string[] origins)
        {
            var options = new AuthInterceptorOptions { OverrideExistingAuthorization = overrideExisting };
            foreach (var origin in origins)
                options.AllowedOrigins.Add(new Uri(origin));
            return new AuthInterceptor(provider, options);
        }

        private static MockTransport CreateTransport(List<Hop> hops, Dictionary<string, string> redirects = null)
        {
            return new MockTransport((req, ctx, ct) =>
            {
                hops.Add(new Hop { Uri = req.Uri.AbsoluteUri, Authorization = req.Headers.Get("Authorization") });

                if (redirects != null && redirects.TryGetValue(req.Uri.AbsoluteUri, out var location))
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", location);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req));
                }

                return Task.FromResult(new UHttpResponse(
                    HttpStatusCode.OK, new HttpHeaders(), Array.Empty<byte>(), ctx.Elapsed, req));
            });
        }

        private static async Task SendAsync(TestInterceptorPipeline pipeline, UHttpRequest request)
        {
            var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        [Test]
        public void AllowList_OnlyAllowedOriginsGetTheToken()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var provider = new CountingTokenProvider();
                var auth = CreateScoped(provider, false, "https://api.test");
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://API.test:443/v1/me")));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://other.test/")));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("http://api.test/")));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://api.test:8443/")));

                Assert.AreEqual("Bearer secret-token", hops[0].Authorization, "allowed origin");
                Assert.IsNull(hops[1].Authorization, "other host");
                Assert.IsNull(hops[2].Authorization, "other scheme");
                Assert.IsNull(hops[3].Authorization, "other port");
                Assert.AreEqual(1, provider.Calls, "the provider is asked only for the allowed origin");
            });
        }

        [Test]
        public void AllowList_SeveralOrigins_EachGetsTheToken()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = CreateScoped(new CountingTokenProvider(), false, "https://a.test/", "http://b.test:8080");
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/x")));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("http://b.test:8080/y")));

                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.AreEqual("Bearer secret-token", hops[1].Authorization);
            });
        }

        [Test]
        public void ExplicitAuthorization_IsLeftUnchangedByDefault()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var provider = new CountingTokenProvider();
                var auth = new AuthInterceptor(provider);
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.test/")).WithBearerToken("caller-token");
                await SendAsync(pipeline, request);

                Assert.AreEqual("Bearer caller-token", hops[0].Authorization);
                Assert.AreEqual(0, provider.Calls, "the provider is not asked when the request carries Authorization");
            });
        }

        [Test]
        public void ExplicitAuthorization_IsReplacedWhenOverrideIsSet()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = new AuthInterceptor(
                    new CountingTokenProvider(),
                    new AuthInterceptorOptions { OverrideExistingAuthorization = true });
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://api.test/")).WithBearerToken("caller-token");
                await SendAsync(pipeline, request);

                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.AreEqual("Bearer caller-token", request.Headers.Get("Authorization"), "the caller's request is not mutated");
            });
        }

        [Test]
        public void Override_DoesNotAttachOutsideTheAllowList()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = CreateScoped(new CountingTokenProvider(), true, "https://api.test");
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://other.test/")).WithBearerToken("caller-token"));

                Assert.AreEqual("Bearer caller-token", hops[0].Authorization);
            });
        }

        [Test]
        public void ExplicitAuthorization_StillBindsTheOrigin_SoACrossOriginRedirectGetsNoToken()
        {
            // The first hop is skipped (caller header), but the redirect guard must still bind to its
            // origin: the redirect strips the caller header and the other host must not get the token.
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = new AuthInterceptor(new CountingTokenProvider());
                var transport = CreateTransport(hops, new Dictionary<string, string>
                {
                    ["https://api.test/start"] = "https://evil.test/landing"
                });
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), auth }, transport);

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://api.test/start")).WithBearerToken("caller-token"));

                Assert.AreEqual(2, hops.Count);
                Assert.AreEqual("Bearer caller-token", hops[0].Authorization);
                Assert.IsNull(hops[1].Authorization);
            });
        }

        [Test]
        public void AllowList_KeepsThePerRequestRedirectGuard()
        {
            // Both origins are allowed, but a redirect hop to an origin other than the request's own
            // still carries no token (RFC 9110 section 15.4); returning to it carries the token again.
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = CreateScoped(new CountingTokenProvider(), false, "https://a.test", "https://b.test");
                var transport = CreateTransport(hops, new Dictionary<string, string>
                {
                    ["https://a.test/start"] = "https://b.test/hop",
                    ["https://b.test/hop"] = "https://a.test/final"
                });
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), auth }, transport);

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://a.test/start")));

                Assert.AreEqual(3, hops.Count);
                Assert.AreEqual("Bearer secret-token", hops[0].Authorization);
                Assert.IsNull(hops[1].Authorization);
                Assert.AreEqual("Bearer secret-token", hops[2].Authorization);
            });
        }

        [Test]
        public void AllowList_RedirectFromADisallowedOriginGetsNoToken()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var auth = CreateScoped(new CountingTokenProvider(), false, "https://api.test");
                var transport = CreateTransport(hops, new Dictionary<string, string>
                {
                    ["https://other.test/start"] = "https://api.test/target"
                });
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { new RedirectInterceptor(), auth }, transport);

                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://other.test/start")));

                Assert.AreEqual(2, hops.Count);
                Assert.IsNull(hops[0].Authorization);
                Assert.IsNull(hops[1].Authorization, "the token is bound to the request's own origin");
            });
        }

        [Test]
        public void Options_AreCopiedAtConstruction()
        {
            AssertAsync.Run(async () =>
            {
                var hops = new List<Hop>();
                var options = new AuthInterceptorOptions { Scheme = "ApiKey" };
                options.AllowedOrigins.Add(new Uri("https://api.test"));
                var auth = new AuthInterceptor(new CountingTokenProvider(), options);
                options.AllowedOrigins.Add(new Uri("https://other.test"));
                options.Scheme = "Changed";

                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { auth }, CreateTransport(hops));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://api.test/")));
                await SendAsync(pipeline, new UHttpRequest(HttpMethod.GET, new Uri("https://other.test/")));

                Assert.AreEqual("ApiKey secret-token", hops[0].Authorization);
                Assert.IsNull(hops[1].Authorization);
            });
        }

        [Test]
        public void Options_RejectInvalidValues()
        {
            var provider = new CountingTokenProvider();
            Assert.Throws<ArgumentNullException>(() => new AuthInterceptor(provider, (AuthInterceptorOptions)null));
            Assert.Throws<ArgumentNullException>(() => new AuthInterceptor(null, new AuthInterceptorOptions()));
            Assert.Throws<ArgumentException>(() => new AuthInterceptor(provider, new AuthInterceptorOptions { Scheme = "Bearer\r\nX: y" }));

            foreach (var bad in new[]
                     {
                         null,
                         new Uri("/relative", UriKind.Relative),
                         new Uri("ftp://api.test/"),
                         new Uri("https://api.test/v1"),
                         new Uri("https://api.test/?q=1"),
                         new Uri("https://api.test/#f"),
                         new Uri("https://user:pass@api.test/")
                     })
            {
                var options = new AuthInterceptorOptions();
                options.AllowedOrigins.Add(bad);
                Assert.Throws<ArgumentException>(
                    () => new AuthInterceptor(provider, options),
                    bad == null ? "null" : bad.OriginalString);
            }
        }
    }
}
