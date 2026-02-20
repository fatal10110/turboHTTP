using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Extensibility
{
    [TestFixture]
    public class PluginRegistryTests
    {
        [Test]
        public void ReadOnlyCapability_AllowsObserverInterceptor()
        {
            Task.Run(async () =>
            {
                var transport = new MockTransport((request, context, ct) =>
                {
                    return Task.FromResult(new UHttpResponse(
                        HttpStatusCode.OK,
                        new HttpHeaders(),
                        Array.Empty<byte>(),
                        context.Elapsed,
                        request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                await client.RegisterPluginAsync(new ObserverPlugin());
                var response = await client.Get("https://example.test/ext").SendAsync();

                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual(1, client.GetRegisteredPlugins().Count);
            }).GetAwaiter().GetResult();
        }

        private sealed class ObserverPlugin : IHttpPlugin
        {
            public string Name => "observer";
            public string Version => "1.0.0";
            public PluginCapabilities Capabilities => PluginCapabilities.ReadOnlyMonitoring;

            public ValueTask InitializeAsync(PluginContext context, CancellationToken cancellationToken)
            {
                context.RegisterInterceptor(new ObserverInterceptor());
                return default;
            }

            public ValueTask ShutdownAsync(CancellationToken cancellationToken)
            {
                return default;
            }
        }

        private sealed class ObserverInterceptor : IHttpInterceptor
        {
            public ValueTask<InterceptorRequestResult> OnRequestAsync(
                UHttpRequest request,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                return new ValueTask<InterceptorRequestResult>(InterceptorRequestResult.Continue());
            }

            public ValueTask<InterceptorResponseResult> OnResponseAsync(
                UHttpRequest request,
                UHttpResponse response,
                RequestContext context,
                CancellationToken cancellationToken)
            {
                return new ValueTask<InterceptorResponseResult>(InterceptorResponseResult.Continue());
            }
        }
    }
}
