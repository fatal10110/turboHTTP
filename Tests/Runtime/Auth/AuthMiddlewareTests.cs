using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Auth
{
    public class AuthMiddlewareTests
    {
        [Test]
        public async Task AddsAuthorizationHeader()
        {
            var provider = new StaticTokenProvider("test-token-123");
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(
                "Bearer test-token-123",
                transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task CustomScheme()
        {
            var provider = new StaticTokenProvider("api-key-456");
            var middleware = new AuthMiddleware(provider, scheme: "ApiKey");
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.AreEqual(
                "ApiKey api-key-456",
                transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task EmptyToken_SkipsHeader()
        {
            var provider = new StaticTokenProvider("");
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public async Task NullToken_SkipsHeader()
        {
            var provider = new StaticTokenProvider(null);
            var middleware = new AuthMiddleware(provider);
            var transport = new MockTransport();
            var pipeline = new HttpPipeline(new[] { middleware }, transport);

            var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
            var context = new RequestContext(request);

            await pipeline.ExecuteAsync(request, context);

            Assert.IsNull(transport.LastRequest.Headers.Get("Authorization"));
        }

        [Test]
        public void NullTokenProvider_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new AuthMiddleware(null));
        }
    }
}
