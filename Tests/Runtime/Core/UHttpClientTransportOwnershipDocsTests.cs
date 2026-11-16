using System;
using System.Net;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Core
{
    /// <summary>
    /// Pins the transport ownership that APIReference.md and Core.md document (#594): a client with
    /// default options uses the shared default transport and does not dispose it; a custom transport
    /// is disposed only with <see cref="UHttpClientOptions.DisposeTransport"/>.
    /// </summary>
    [TestFixture]
    public class UHttpClientTransportOwnershipDocsTests
    {
        [TearDown]
        public void TearDown() => HttpTransportFactory.Reset();

        [Test]
        public void DefaultOptionsClient_Dispose_LeavesSharedTransportOpen()
        {
            AssertAsync.Run(async () =>
            {
                var shared = new MockTransport();
                shared.EnqueueResponse(HttpStatusCode.OK);
                HttpTransportFactory.SetForTesting(shared);

                var first = new UHttpClient();
                first.Dispose();

                Assert.AreSame(shared, HttpTransportFactory.Default);
                var second = new UHttpClient();
                using var response = await second.Get("https://example.test/").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                    "the shared transport must still serve requests after a default client was disposed");
                second.Dispose();
            });
        }

        [Test]
        public void CustomTransport_Dispose_KeepsTransportUsableWithoutDisposeTransport()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK);
                var client = new UHttpClient(new UHttpClientOptions { Transport = transport, FollowRedirects = false });
                client.Dispose();

                var other = new UHttpClient(new UHttpClientOptions { Transport = transport, FollowRedirects = false });
                using var response = await other.Get("https://example.test/").SendBufferedAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                other.Dispose();
            });
        }

        [Test]
        public void CustomTransport_Dispose_DisposesWithDisposeTransport()
        {
            AssertAsync.Run(async () =>
            {
                var transport = new MockTransport();
                transport.EnqueueResponse(HttpStatusCode.OK);
                var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    FollowRedirects = false
                });
                client.Dispose();

                var other = new UHttpClient(new UHttpClientOptions { Transport = transport, FollowRedirects = false });
                var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
                {
                    using var r = await other.Get("https://example.test/").SendBufferedAsync();
                });
                Assert.IsInstanceOf<ObjectDisposedException>(ex.HttpError.InnerException);
                other.Dispose();
            });
        }
    }
}
