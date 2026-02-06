using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class SslStreamProviderTests
    {
        [Test]
        public void ProviderName_ReturnsSslStream()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }

        [Test]
        public void IsAlpnSupported_ReturnsBoolean()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            // Result depends on platform/.NET version, but should not throw
            var result = provider.IsAlpnSupported();
            Assert.That(result, Is.TypeOf<bool>());
        }

        [Test]
        public void Instance_ReturnsSameInstance()
        {
            var provider1 = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            var provider2 = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            Assert.AreSame(provider1, provider2, "SslStream provider should be a singleton");
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        public async Task WrapAsync_RealServer_SucceedsWithTls12OrHigher()
        {
            // Integration test: connect to a real HTTPS server
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream, 
                System.Net.Sockets.ProtocolType.Tcp);
            
            await socket.ConnectAsync("httpbin.org", 443);
            using var stream = new System.Net.Sockets.NetworkStream(socket, true);

            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            var result = await provider.WrapAsync(
                stream, "httpbin.org", new[] { "h2", "http/1.1" }, CancellationToken.None);

            Assert.IsNotNull(result.SecureStream);
            Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
            Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1").Or.Null);
            Assert.AreEqual("SslStream", result.ProviderName);
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        public async Task WrapAsync_CancellationRequested_ThrowsOperationCanceledException()
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream, 
                System.Net.Sockets.ProtocolType.Tcp);
            
            await socket.ConnectAsync("httpbin.org", 443);
            using var stream = new System.Net.Sockets.NetworkStream(socket, true);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);

            Assert.ThrowsAsync<TaskCanceledException>(async () =>
                await provider.WrapAsync(stream, "httpbin.org", new[] { "h2" }, cts.Token));
        }
    }
}
