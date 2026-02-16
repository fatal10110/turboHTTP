using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    [TestFixture]
    public class RedirectMiddlewareTests
    {
        [Test]
        public void RedirectMiddleware_FollowsRedirectChain()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        var redirectHeaders = new HttpHeaders();
                        redirectHeaders.Set("Location", "/final");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            redirectHeaders,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Encoding.UTF8.GetBytes("done"),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("done", Encoding.UTF8.GetString(response.Body.Span));
                Assert.AreEqual(2, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_EnforcesMaxRedirects()
        {
            Task.Run(() =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", "/loop");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware(defaultFollowRedirects: true, defaultMaxRedirects: 1);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/loop"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect limit exceeded", ex.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_RewritesPostToGet_On302()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/submit")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/result");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual(HttpMethod.GET, req.Method);
                    Assert.IsNull(req.Body);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("body"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_PreservesMethodAndBody_On307()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/submit")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/result");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.TemporaryRedirect,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual(HttpMethod.POST, req.Method);
                    Assert.AreEqual("body", Encoding.UTF8.GetString(req.Body));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("body"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_StripsAuthorizationOnCrossOriginRedirect()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.Host == "origin.test")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "https://other.test/final");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.IsNull(req.Headers.Get("Authorization"));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var headersWithAuth = new HttpHeaders();
                headersWithAuth.Set("Authorization", "Bearer secret");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://origin.test/start"), headersWithAuth);

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_ResolvesRelativeLocation()
        {
            Task.Run(async () =>
            {
                Uri finalRequestUri = null;

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/a/start")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "../final?x=1");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    finalRequestUri = req.Uri;
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a/start"));
                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.IsNotNull(finalRequestUri);
                Assert.AreEqual("https://example.test/final?x=1", finalRequestUri.AbsoluteUri);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_DetectsRedirectLoops()
        {
            Task.Run(() =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", req.Uri.AbsolutePath == "/a" ? "/b" : "/a");

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect loop detected", ex.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_CanBeDisabledPerRequestMetadata()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    headers.Set("Location", "/final");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var metadata = new Dictionary<string, object>
                {
                    [RequestMetadataKeys.FollowRedirects] = false
                };

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://example.test/start"),
                    metadata: metadata);

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.Found, response.StatusCode);
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_BlocksHttpsToHttpDowngrade_ByDefault()
        {
            Task.Run(() =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "http://example.test/insecure");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Blocked insecure redirect downgrade", ex.Message);
                Assert.AreEqual(1, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_AllowsHttpsToHttpDowngrade_WhenEnabled()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "http://example.test/insecure");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual("http", req.Uri.Scheme);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware(defaultAllowHttpsToHttpDowngrade: true);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_DetectsLoop_WhenStatusCodesDifferAcrossHops()
        {
            Task.Run(() =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    if (req.Uri.AbsolutePath == "/a")
                    {
                        headers.Set("Location", "/b");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.MovedPermanently,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    headers.Set("Location", "/a");
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.Found,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectMiddleware(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect loop detected", ex.Message);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void RedirectMiddleware_EnforcesTotalTimeoutAcrossRedirectHops()
        {
            Task.Run(() =>
            {
                var transport = new MockTransport(async (req, ctx, ct) =>
                {
                    var headers = new HttpHeaders();
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        headers.Set("Location", "/step1");
                        return new UHttpResponse(
                            HttpStatusCode.Found,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req);
                    }

                    await Task.Delay(120, ct).ConfigureAwait(false);
                    headers.Set("Location", "/final");
                    return new UHttpResponse(
                        HttpStatusCode.Found,
                        headers,
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req);
                });

                var middleware = new RedirectMiddleware(defaultEnforceRedirectTotalTimeout: true);
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://example.test/start"),
                    timeout: TimeSpan.FromMilliseconds(100));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                StringAssert.Contains("Redirect chain exceeded total timeout budget", ex.Message);
                Assert.AreEqual(2, transport.RequestCount);
            }).GetAwaiter().GetResult();
        }
    }
}
