using System;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Pipeline
{
    public class DefaultHeadersMiddlewareTests
    {
        [Test]
        public void AddsDefaultHeaders()        {
            Task.Run(async () =>
            {
                var defaults = new HttpHeaders();
                defaults.Set("X-Custom", "DefaultValue");
                defaults.Set("Accept", "application/json");

                var middleware = new DefaultHeadersMiddleware(defaults);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual("DefaultValue", transport.LastRequest.Headers.Get("X-Custom"));
                Assert.AreEqual("application/json", transport.LastRequest.Headers.Get("Accept"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DoesNotOverrideExistingHeaders()        {
            Task.Run(async () =>
            {
                var defaults = new HttpHeaders();
                defaults.Set("Accept", "application/json");

                var middleware = new DefaultHeadersMiddleware(defaults);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var headers = new HttpHeaders();
                headers.Set("Accept", "text/html");
                var request = new UHttpRequest(
                    HttpMethod.GET, new Uri("https://test.com"), headers);
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                // Request header takes precedence
                Assert.AreEqual("text/html", transport.LastRequest.Headers.Get("Accept"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void OverridesWhenConfigured()        {
            Task.Run(async () =>
            {
                var defaults = new HttpHeaders();
                defaults.Set("Accept", "application/json");

                var middleware = new DefaultHeadersMiddleware(defaults, overrideExisting: true);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var headers = new HttpHeaders();
                headers.Set("Accept", "text/html");
                var request = new UHttpRequest(
                    HttpMethod.GET, new Uri("https://test.com"), headers);
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                Assert.AreEqual("application/json", transport.LastRequest.Headers.Get("Accept"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void DoesNotModifyOriginalRequest()        {
            Task.Run(async () =>
            {
                var defaults = new HttpHeaders();
                defaults.Set("X-Added", "value");

                var middleware = new DefaultHeadersMiddleware(defaults);
                var transport = new MockTransport();
                var pipeline = new HttpPipeline(new[] { middleware }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://test.com"));
                var context = new RequestContext(request);

                await pipeline.ExecuteAsync(request, context);

                // Original request should NOT have the added header
                Assert.IsNull(request.Headers.Get("X-Added"));
                // Transport should have received it
                Assert.AreEqual("value", transport.LastRequest.Headers.Get("X-Added"));
            }).GetAwaiter().GetResult();
        }
    }
}
