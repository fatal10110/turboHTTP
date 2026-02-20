using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Mobile
{
    [TestFixture]
    public class BackgroundNetworkingTests
    {
        [Test]
        public void PolicyDisabled_NoBehaviorChange()
        {
            Task.Run(async () =>
            {
                var middleware = new BackgroundNetworkingMiddleware(new BackgroundNetworkingPolicy
                {
                    Enable = false
                });

                var request = new UHttpRequest(HttpMethod.GET, new Uri("https://example.test/mobile"));
                var context = new RequestContext(request);
                var transport = new MockTransport();

                var response = await middleware.InvokeAsync(
                    request,
                    context,
                    (req, ctx, ct) => transport.SendAsync(req, ctx, ct),
                    CancellationToken.None);

                Assert.IsTrue(response.IsSuccessStatusCode);
                Assert.AreEqual(0, middleware.Queued);
            }).GetAwaiter().GetResult();
        }
    }
}
