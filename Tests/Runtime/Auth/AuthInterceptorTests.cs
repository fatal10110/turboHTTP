using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Middleware;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    public class AuthInterceptorTests
    {
        [Test]
        public void AddsAuthorizationHeader()        {
            Task.Run(async () =>
            {
                var provider = new StaticTokenProvider("test-token-123");
                var middleware = new AuthInterceptor(provider);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(
                    "Bearer test-token-123",
                    transport.LastRequest.Headers.Get("Authorization"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void CustomScheme()        {
            Task.Run(async () =>
            {
                var provider = new StaticTokenProvider("api-key-456");
                var middleware = new AuthInterceptor(provider, scheme: "ApiKey");
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual(
                    "ApiKey api-key-456",
                    transport.LastRequest.Headers.Get("Authorization"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void EmptyToken_SkipsHeader()        {
            Task.Run(async () =>
            {
                var provider = new StaticTokenProvider("");
                var middleware = new AuthInterceptor(provider);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NullToken_SkipsHeader()        {
            Task.Run(async () =>
            {
                var provider = new StaticTokenProvider(null);
                var middleware = new AuthInterceptor(provider);
                var transport = new MockTransport();
                var pipeline = new TestInterceptorPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void NullTokenProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new AuthInterceptor(null));
        }
    }
}
