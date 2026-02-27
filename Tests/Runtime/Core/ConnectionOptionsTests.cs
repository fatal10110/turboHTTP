using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;

namespace TurboHTTP.Tests.Core
{
    [TestFixture]
    public class ConnectionOptionsTests
    {
        [Test]
        public void DefaultOptions_ShouldReuseDefaultTransport()
        {
            var options = new UHttpClientOptions();
            using var client = new UHttpClient(options);
            
            // Should reuse the factory default transport
            Assert.That(client.Transport, Is.SameAs(HttpTransportFactory.Default));
        }

        [Test]
        public void CustomConnectionPoolOptions_ShouldAllocateDedicatedTransport()
        {
            var options = new UHttpClientOptions();
            options.ConnectionPool.ConnectionIdleTimeout = TimeSpan.FromMinutes(10);
            
            using var client = new UHttpClient(options);
            
            // Should allocate a new transport instead of reusing default
            Assert.That(client.Transport, Is.Not.SameAs(HttpTransportFactory.Default));
            Assert.That(client.Transport, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void CustomHttp2Options_ShouldAllocateDedicatedTransport()
        {
            var options = new UHttpClientOptions();
            options.Http2.MaxResponseBodySize = 1024 * 1024;
            
            using var client = new UHttpClient(options);
            
            // Should allocate a new transport instead of reusing default
            Assert.That(client.Transport, Is.Not.SameAs(HttpTransportFactory.Default));
            Assert.That(client.Transport, Is.InstanceOf<RawSocketTransport>());
        }

        [Test]
        public void Http2MaxDecodedHeaderBytes_ShouldBeConfiguredOnHttp2Options()
        {
            var options = new UHttpClientOptions();
            options.Http2.MaxDecodedHeaderBytes = 50000;

            Assert.That(options.Http2.MaxDecodedHeaderBytes, Is.EqualTo(50000));
        }
    }
}
