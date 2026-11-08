using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Testing;
using TurboHTTP.Unity.Mobile;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Phase 22a.6 Step 1, request mutation items 27-28 (#102): interceptors that change only headers
    /// (<see cref="AuthInterceptor"/>) or only the timeout (<see cref="AdaptiveInterceptor"/>) hand the
    /// transport a copy that shares the caller's request content: the same body instance, never a second
    /// reader or a buffered duplicate of a streaming body, and the caller's request is left unchanged.
    /// </summary>
    [TestFixture]
    public sealed class SharedContentMutationTests
    {
        [Test]
        public void AuthInterceptor_HeaderOnlyMutation_SharesTheStreamingRequestContent()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new CapturingTransport();
                using var client = CreateClient(transport, new AuthInterceptor(new StaticTokenProvider("t0ken")));
                var request = client.Post("https://shared.local/auth").WithStreamBody(new MemoryStream(new byte[] { 1, 2, 3 }));
                var originalContent = request.Content;

                using var response = await request.SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreNotSame(request, transport.Request, "the interceptor must not mutate the caller's request");
                Assert.AreEqual("Bearer t0ken", transport.Request.Headers.Get("Authorization"));
                Assert.IsNull(request.Headers.Get("Authorization"));
                Assert.AreSame(originalContent, transport.Content, "a header-only change must share the request content");
            });
        }

        [Test]
        public void AdaptiveInterceptor_TimeoutOnlyMutation_SharesTheStreamingRequestContent()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new CapturingTransport();
                var policy = new AdaptivePolicy
                {
                    Enable = true,
                    MinTimeout = TimeSpan.FromSeconds(1),
                    MaxTimeout = TimeSpan.FromSeconds(10)
                };
                using var client = CreateClient(
                    transport,
                    new AdaptiveInterceptor(policy, new NetworkQualityDetector()),
                    defaultTimeout: TimeSpan.FromSeconds(20));
                var request = client.Post("https://shared.local/adaptive").WithStreamBody(new MemoryStream(new byte[] { 1, 2, 3 }));
                var originalContent = request.Content;
                var originalTimeout = request.Timeout;

                using var response = await request.SendBufferedAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreNotSame(request, transport.Request, "the adapted timeout must go on a copy");
                Assert.LessOrEqual(transport.Request.Timeout, policy.MaxTimeout, "the timeout was not adapted");
                Assert.AreEqual(originalTimeout, request.Timeout);
                Assert.AreSame(originalContent, transport.Content, "a timeout-only change must share the request content");
            });
        }

        private static UHttpClient CreateClient(IHttpTransport transport, IHttpInterceptor interceptor, TimeSpan? defaultTimeout = null)
        {
            var options = new UHttpClientOptions
            {
                Transport = transport,
                DisposeTransport = true,
                FollowRedirects = false,
                Interceptors = new List<IHttpInterceptor> { interceptor }
            };
            if (defaultTimeout.HasValue)
                options.DefaultTimeout = defaultTimeout.Value;
            return new UHttpClient(options);
        }

        private sealed class CapturingTransport : IHttpTransport
        {
            internal UHttpRequest Request { get; private set; }
            internal UHttpRequestBody Content { get; private set; }

            public Task DispatchAsync(UHttpRequest request, IHttpHandler handler, RequestContext context, CancellationToken cancellationToken = default)
            {
                Request = request;
                Content = request.Content;
                handler.OnRequestStart(request, context);
                return handler.OnResponseStartAsync(
                        (int)HttpStatusCode.OK,
                        new HttpHeaders(),
                        new MockResponseBodySource(ReadOnlyMemory<byte>.Empty),
                        context)
                    .AsTask();
            }

            public void Dispose()
            {
            }
        }
    }
}
