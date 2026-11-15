using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Cache;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Cache
{
    /// <summary>
    /// Regression tests for #494 and #495: the cache stores a response only for the request that elicited it, as it
    /// left the pipeline. A redirect followed inside the cache is never stored under the original URL, and headers
    /// added by interceptors inside the cache drive the Authorization guard and the Vary check.
    /// </summary>
    /// <remarks>
    /// UHttpClient rejects a cache outside the redirect interceptor (#536), so the redirect tests compose the
    /// pipeline directly, as a custom pipeline or a third-party redirect interceptor without a role could.
    /// </remarks>
    [TestFixture]
    public class CacheEffectiveRequestTests
    {
        private static readonly Uri Original = new Uri("https://cache.test/old");
        private static readonly Uri Target = new Uri("https://cache.test/new");

        [Test]
        public void RedirectInsideCache_TargetResponseIsNotStoredUnderOriginalUrl()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                    Task.FromResult(req.Uri.AbsolutePath == "/old"
                        ? Redirect(req, ctx, Target)
                        : Ok(req, ctx, "max-age=60", "\"t\"", "target")));

                using var cache = new CacheInterceptor(new CachePolicy());
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache, new RedirectInterceptor() },
                    transport);

                using (var first = await SendAsync(pipeline, Original))
                {
                    Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
                    Assert.AreEqual("target", Encoding.UTF8.GetString(first.Body.ToArray()));
                }

                using (var second = await SendAsync(pipeline, Original))
                    Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);

                // Both calls followed the redirect on the wire: the target's response was not stored under /old.
                Assert.AreEqual(4, transport.RequestCount);
            });
        }

        [Test]
        public void RedirectInsideCache_OnRevalidation_TargetResponseIsNotStoredUnderOriginalUrl()
        {
            AssertAsync.Run(async () =>
            {
                int originalHits = 0;
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath != "/old")
                        return Task.FromResult(Ok(req, ctx, "max-age=60", "\"t\"", "target"));

                    // First: a stored but immediately stale response. Later (the conditional request included): a redirect.
                    return Task.FromResult(Interlocked.Increment(ref originalHits) == 1
                        ? Ok(req, ctx, "max-age=0", "\"v1\"", "original")
                        : Redirect(req, ctx, Target));
                });

                using var cache = new CacheInterceptor(new CachePolicy { EnableRevalidation = true });
                var pipeline = new TestInterceptorPipeline(
                    new IHttpInterceptor[] { cache, new RedirectInterceptor() },
                    transport);

                using (await SendAsync(pipeline, Original))
                {
                }

                // Revalidation: the conditional request is redirected and the target answers 200.
                using (var second = await SendAsync(pipeline, Original))
                    Assert.AreEqual("target", Encoding.UTF8.GetString(second.Body.ToArray()));

                Assert.AreEqual(3, transport.RequestCount);

                using (var third = await SendAsync(pipeline, Original))
                    Assert.AreEqual(HttpStatusCode.OK, third.StatusCode);

                // The target's 200 was not stored under /old by the revalidation, so the third call went to the wire.
                Assert.AreEqual(5, transport.RequestCount);
            });
        }

        [Test]
        public void AuthorizationAddedInsideCache_ResponseIsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var transport = CacheableTransport(vary: null);
                using var client = CreateClient(
                    transport,
                    new CacheInterceptor(new CachePolicy { AllowCacheForAuthorizedRequests = false }),
                    new AddHeaderInterceptor("Authorization", "Bearer secret-token"));

                await GetTwiceAsync(client);

                Assert.AreEqual("Bearer secret-token", transport.LastRequest.Headers.Get("Authorization"));
                // The cache saw the effective request's Authorization header and refused to store (RFC 9111 3.5).
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void VaryNominatedHeaderAddedInsideCache_ResponseIsNotStored()
        {
            AssertAsync.Run(async () =>
            {
                var transport = CacheableTransport(vary: "X-User");
                using var client = CreateClient(
                    transport,
                    new CacheInterceptor(new CachePolicy()),
                    new AddHeaderInterceptor("X-User", "alice"));

                await GetTwiceAsync(client);

                // Stored under the lookup-time key (no X-User) it would be served to every user (RFC 9111 4.1).
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void HeaderAddedInsideCache_NotNominatedByVary_ResponseIsStillStored()
        {
            AssertAsync.Run(async () =>
            {
                var transport = CacheableTransport(vary: "Accept-Language");
                using var client = CreateClient(
                    transport,
                    new CacheInterceptor(new CachePolicy()),
                    new AddHeaderInterceptor("X-Trace", "1"));

                await GetTwiceAsync(client);

                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        [Test]
        public void DecompressionInsideCache_VaryAcceptEncodingIdentityResponse_IsStillStored()
        {
            AssertAsync.Run(async () =>
            {
                var transport = CacheableTransport(vary: "Accept-Encoding");
                using var client = CreateClient(
                    transport,
                    new CacheInterceptor(new CachePolicy()),
                    new DecompressionInterceptor());

                await GetTwiceAsync(client);

                // The decompression interceptor added Accept-Encoding inside the cache; the identity response
                // satisfies a request without Accept-Encoding (RFC 9110 12.5.3), so it is stored and served.
                Assert.AreEqual("gzip, deflate", transport.LastRequest.Headers.Get("Accept-Encoding"));
                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        private static async Task GetTwiceAsync(UHttpClient client)
        {
            using (var first = await client.Get("https://cache.test/me").SendBufferedAsync())
                Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            using (var second = await client.Get("https://cache.test/me").SendBufferedAsync())
                Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        }

        private static Task<UHttpResponse> SendAsync(TestInterceptorPipeline pipeline, Uri uri)
        {
            var request = new UHttpRequest(HttpMethod.GET, uri);
            return pipeline.ExecuteAsync(request, new RequestContext(request));
        }

        private static UHttpClient CreateClient(MockTransport transport, params IHttpInterceptor[] interceptors)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                Interceptors = new List<IHttpInterceptor>(interceptors)
            });
        }

        private static MockTransport CacheableTransport(string vary)
        {
            var headers = new HttpHeaders();
            headers.Set("Cache-Control", "max-age=60");
            if (vary != null)
                headers.Set("Vary", vary);
            return new MockTransport(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes("per-user"));
        }

        private static UHttpResponse Redirect(UHttpRequest req, RequestContext ctx, Uri location)
        {
            var headers = new HttpHeaders();
            headers.Set("Location", location.AbsoluteUri);
            return new UHttpResponse(HttpStatusCode.Found, headers, Array.Empty<byte>(), ctx.Elapsed, req);
        }

        private static UHttpResponse Ok(UHttpRequest req, RequestContext ctx, string cacheControl, string eTag, string body)
        {
            var headers = new HttpHeaders();
            headers.Set("Cache-Control", cacheControl);
            headers.Set("ETag", eTag);
            return new UHttpResponse(HttpStatusCode.OK, headers, Encoding.UTF8.GetBytes(body), ctx.Elapsed, req);
        }

        /// <summary>
        /// A role-less interceptor placed inside the cache that adds a header to a copy of the request, like a
        /// custom credential interceptor would.
        /// </summary>
        private sealed class AddHeaderInterceptor : IHttpInterceptor
        {
            private readonly string _name;
            private readonly string _value;

            public AddHeaderInterceptor(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public DispatchFunc Wrap(DispatchFunc next)
            {
                return (request, handler, context, cancellationToken) =>
                {
                    var copy = request.CopyWithSharedContent();
                    copy.Headers.Set(_name, _value);
                    context.UpdateRequest(copy);
                    return next(copy, handler, context, cancellationToken);
                };
            }
        }
    }
}
