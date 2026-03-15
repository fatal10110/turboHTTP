using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Middleware
{
    [TestFixture]
    public class RedirectInterceptorTests
    {
        [Test]
        public void RedirectInterceptor_FollowsRedirectChain()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("done", response.GetBodyAsString());
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void RedirectInterceptor_EnforcesMaxRedirects()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor(defaultFollowRedirects: true, defaultMaxRedirects: 1);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/loop"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect limit exceeded", ex.Message);
            });
        }

        [Test]
        public void RedirectInterceptor_RewritesPostToGet_On302()
        {
            AssertAsync.Run(async () =>
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
                    Assert.IsTrue(req.Body.IsEmpty);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("body"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_RewritesPutToGet_On303()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/submit")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/result");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.SeeOther,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual(HttpMethod.GET, req.Method);
                    Assert.IsTrue(req.Body.IsEmpty);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.PUT,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("put-body"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_RewritesDeleteToGet_On303()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/delete")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/after-delete");
                        return Task.FromResult(new UHttpResponse(
                            HttpStatusCode.SeeOther,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual(HttpMethod.GET, req.Method);
                    Assert.IsTrue(req.Body.IsEmpty);
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.DELETE,
                    new Uri("https://example.test/delete"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_PreservesMethodAndBody_On307()
        {
            AssertAsync.Run(async () =>
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
                    Assert.AreEqual("body", Encoding.UTF8.GetString(req.Body.Span));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("body"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_PreservesMethodAndBody_On308()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/submit")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/result");
                        return Task.FromResult(new UHttpResponse(
                            (HttpStatusCode)308,
                            headers,
                            Array.Empty<byte>(),
                            ctx.Elapsed,
                            req));
                    }

                    Assert.AreEqual(HttpMethod.PUT, req.Method);
                    Assert.AreEqual("payload", Encoding.UTF8.GetString(req.Body.Span));
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        ctx.Elapsed,
                        req));
                });

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.PUT,
                    new Uri("https://example.test/submit"),
                    body: Encoding.UTF8.GetBytes("payload"));

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_StripsAuthorizationOnCrossOriginRedirect()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var headersWithAuth = new HttpHeaders();
                headersWithAuth.Set("Authorization", "Bearer secret");
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://origin.test/start"), headersWithAuth);

                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            });
        }

        [Test]
        public void RedirectInterceptor_ResolvesRelativeLocation()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a/start"));
                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.IsNotNull(finalRequestUri);
                Assert.AreEqual("https://example.test/final?x=1", finalRequestUri.AbsoluteUri);
            });
        }

        [Test]
        public void RedirectInterceptor_InheritsFragment_WhenLocationHasNoFragment()
        {
            AssertAsync.Run(async () =>
            {
                Uri finalRequestUri = null;

                var transport = new MockTransport((req, ctx, ct) =>
                {
                    if (req.Uri.AbsolutePath == "/start")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/final");
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

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start#frag"));
                await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.IsNotNull(finalRequestUri);
                Assert.AreEqual("https://example.test/final#frag", finalRequestUri.AbsoluteUri);
            });
        }

        [Test]
        public void RedirectInterceptor_DetectsRedirectLoops()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect loop detected", ex.Message);
            });
        }

        [Test]
        public void RedirectInterceptor_CanBeDisabledPerRequestMetadata()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

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
            });
        }

        [Test]
        public void RedirectInterceptor_BlocksHttpsToHttpDowngrade_ByDefault()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Blocked insecure redirect downgrade", ex.Message);
                Assert.AreEqual(1, transport.RequestCount);
            });
        }

        [Test]
        public void RedirectInterceptor_AllowsHttpsToHttpDowngrade_WhenEnabled()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor(defaultAllowHttpsToHttpDowngrade: true);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var response = await pipeline.ExecuteAsync(request, new RequestContext(request));

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void RedirectInterceptor_ClonesPreservedBodyAcrossRedirectHop()
        {
            AssertAsync.Run(async () =>
            {
                UHttpRequest redirectedRequest = null;
                var originalBody = Encoding.UTF8.GetBytes("payload");
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    handler.OnRequestStart(req, ctx);

                    if (req.Uri.AbsolutePath == "/start")
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/next");
                        handler.OnResponseStart((int)HttpStatusCode.TemporaryRedirect, headers, ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                        return Task.CompletedTask;
                    }

                    redirectedRequest = req;
                    handler.OnResponseStart((int)HttpStatusCode.OK, new HttpHeaders(), ctx);
                    handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                    return Task.CompletedTask;
                });

                var pipeline = new TestInterceptorPipeline(new[] { new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(
                    HttpMethod.POST,
                    new Uri("https://example.test/start"),
                    body: originalBody);

                using var _ = await pipeline.ExecuteAsync(request, new RequestContext(request));

                originalBody[0] = (byte)'X';

                Assert.NotNull(redirectedRequest);
                Assert.AreEqual(HttpMethod.POST, redirectedRequest.Method);
                Assert.AreEqual("payload", Encoding.UTF8.GetString(redirectedRequest.Body.ToArray()));
            });
        }

        [Test]
        public void RedirectInterceptor_DetectsLoop_WhenStatusCodesDifferAcrossHops()
        {
            AssertAsync.Run(async () =>
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

                var middleware = new RedirectInterceptor(defaultFollowRedirects: true, defaultMaxRedirects: 10);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/a"));
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type);
                StringAssert.Contains("Redirect loop detected", ex.Message);
            });
        }

        [Test]
        public void RedirectInterceptor_EnforcesTotalTimeoutAcrossRedirectHops()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport(
                    (Func<UHttpRequest, RequestContext, CancellationToken, ValueTask<UHttpResponse>>)(async (req, ctx, ct) =>
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
                }));

                var middleware = new RedirectInterceptor(defaultEnforceRedirectTotalTimeout: true);
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(
                    HttpMethod.GET,
                    new Uri("https://example.test/start"),
                    timeout: TimeSpan.FromMilliseconds(100));

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                    await pipeline.ExecuteAsync(request, new RequestContext(request)));

                Assert.AreEqual(UHttpErrorType.Timeout, ex.HttpError.Type);
                StringAssert.Contains("Redirect chain exceeded total timeout budget", ex.Message);
                Assert.AreEqual(2, transport.RequestCount);
            });
        }

        [Test]
        public void RedirectInterceptor_FailsWhenRedirectDispatchCompletesWithoutTerminalCallback()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    callCount++;
                    handler.OnRequestStart(req, ctx);

                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/final");
                        handler.OnResponseStart((int)HttpStatusCode.Found, headers, ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                        return Task.CompletedTask;
                    }

                    return Task.CompletedTask;
                });

                var pipeline = new TestInterceptorPipeline(new[] { new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var context = new RequestContext(request);

                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    using var _ = await pipeline.ExecuteAsync(request, context);
                });

                Assert.That(ex.HttpError.Message, Does.Contain("terminal callback"));
                Assert.AreEqual(2, callCount);
            });
        }

        [Test]
        public void RedirectInterceptor_SynchronousRedirectDispatchFault_CompletesWithOnResponseError()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    callCount++;
                    handler.OnRequestStart(req, ctx);

                    if (callCount == 1)
                    {
                        var headers = new HttpHeaders();
                        headers.Set("Location", "/final");
                        handler.OnResponseStart((int)HttpStatusCode.Found, headers, ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                        return Task.CompletedTask;
                    }

                    throw new InvalidOperationException("redirect dispatch failed");
                });

                var pipeline = new InterceptorPipeline(new[] { new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var context = new RequestContext(request);
                var handler = new RecordingErrorHandler();

                await pipeline.Pipeline(request, handler, context, CancellationToken.None);

                Assert.AreEqual(2, handler.RequestStartCount);
                Assert.IsFalse(handler.EndCalled);
                Assert.IsNotNull(handler.LastError);
                Assert.That(handler.LastError.Message, Does.Contain("redirect dispatch failed"));
            });
        }

        [Test]
        public void RedirectInterceptor_PreservesDownstreamOnResponseEndFailure()
        {
            AssertAsync.Run(async () =>
            {
                int callCount = 0;
                var transport = new CallbackTransport((req, handler, ctx, ct) =>
                {
                    callCount++;
                    handler.OnRequestStart(req, ctx);

                    try
                    {
                        if (callCount == 1)
                        {
                            var redirectHeaders = new HttpHeaders();
                            redirectHeaders.Set("Location", "/final");
                            handler.OnResponseStart((int)HttpStatusCode.Found, redirectHeaders, ctx);
                            handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                            return Task.CompletedTask;
                        }

                        handler.OnResponseStart((int)HttpStatusCode.OK, new HttpHeaders(), ctx);
                        handler.OnResponseEnd(HttpHeaders.Empty, ctx);
                    }
                    catch
                    {
                        // Simulate a transport that swallows downstream callback failures.
                    }

                    return Task.CompletedTask;
                });

                var pipeline = new InterceptorPipeline(new[] { new RedirectInterceptor() }, transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/start"));
                var context = new RequestContext(request);

                var ex = AssertAsync.ThrowsAsync<InvalidOperationException>(async () =>
                    await pipeline.Pipeline(request, new ThrowOnEndHandler(), context, CancellationToken.None));

                Assert.That(ex.Message, Does.Contain("downstream end failed"));
                Assert.AreEqual(2, callCount);
            });
        }

        private sealed class CallbackTransport : IHttpTransport
        {
            private readonly Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> _dispatch;

            internal CallbackTransport(Func<UHttpRequest, IHttpHandler, RequestContext, CancellationToken, Task> dispatch)
            {
                _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
            }

            public Task DispatchAsync(
                UHttpRequest request,
                IHttpHandler handler,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                return _dispatch(request, handler, context, cancellationToken);
            }

            public ValueTask<UHttpResponse> SendAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }
        }

        private sealed class ThrowOnEndHandler : IHttpHandler
        {
            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                throw new InvalidOperationException("downstream end failed");
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
            }
        }

        private sealed class RecordingErrorHandler : IHttpHandler
        {
            public int RequestStartCount { get; private set; }
            public bool EndCalled { get; private set; }
            public UHttpException LastError { get; private set; }

            public void OnRequestStart(UHttpRequest request, RequestContext context)
            {
                RequestStartCount++;
            }

            public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
            {
            }

            public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
            {
            }

            public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
            {
                EndCalled = true;
            }

            public void OnResponseError(UHttpException error, RequestContext context)
            {
                LastError = error;
            }
        }
    }
}
