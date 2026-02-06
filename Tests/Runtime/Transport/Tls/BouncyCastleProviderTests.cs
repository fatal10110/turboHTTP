using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class BouncyCastleProviderTests
    {
        private bool IsAvailable() => TlsProviderSelector.IsBouncyCastleAvailable();

        [Test]
        public void ProviderName_ReturnsBouncyCastle()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreEqual("BouncyCastle", provider.ProviderName);
        }

        [Test]
        public void IsAlpnSupported_AlwaysReturnsTrue()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.IsTrue(provider.IsAlpnSupported(), 
                "BouncyCastle should always report ALPN support");
        }

        [Test]
        public void GetProvider_CachesInstance()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider1 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var provider2 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreSame(provider1, provider2, 
                "BouncyCastle provider should be cached via Lazy<T>");
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        public async Task WrapAsync_RealServer_SucceedsWithH2Alpn()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);

            await socket.ConnectAsync("httpbin.org", 443);
            using var stream = new System.Net.Sockets.NetworkStream(socket, true);

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var result = await provider.WrapAsync(
                stream, "httpbin.org", new[] { "h2", "http/1.1" }, CancellationToken.None);

            Assert.IsNotNull(result.SecureStream);
            Assert.AreEqual("BouncyCastle", result.ProviderName);
            Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
            Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1"));
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        public async Task WrapAsync_SniSent_ServerAcceptsConnection()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            // Test SNI by connecting to a server that requires it
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);

            await socket.ConnectAsync("www.google.com", 443);
            using var stream = new System.Net.Sockets.NetworkStream(socket, true);

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var result = await provider.WrapAsync(
                stream, "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);

            Assert.IsNotNull(result.SecureStream, "SNI should allow successful handshake");
        }

        [Test]
        [Explicit("Requires network access - tests certificate hostname mismatch")]
        public void WrapAsync_HostnameMismatch_ThrowsAuthenticationException()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            // Connect to google.com but claim we wanted example.com
            // This should fail certificate validation due to hostname mismatch
            Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(async () =>
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);

                await socket.ConnectAsync("www.google.com", 443);
                using var stream = new System.Net.Sockets.NetworkStream(socket, true);

                var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
                // Pass wrong hostname - certificate is for *.google.com, not example.com
                await provider.WrapAsync(stream, "example.com", new[] { "h2" }, CancellationToken.None);
            });
        }

        [Test]
        public async Task WrapAsync_PreCancelledToken_ThrowsOperationCancelledException()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            using var cts = new CancellationTokenSource();
            cts.Cancel();  // Pre-cancel

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            using var memoryStream = new System.IO.MemoryStream();

            // Should throw immediately without attempting handshake
            Assert.ThrowsAsync<System.OperationCanceledException>(async () =>
                await provider.WrapAsync(memoryStream, "example.com", new[] { "h2" }, cts.Token));
        }

        [Test]
        public void WrapAsync_NullStream_ThrowsArgumentNullException()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);

            Assert.ThrowsAsync<System.ArgumentNullException>(async () =>
                await provider.WrapAsync(null, "example.com", new[] { "h2" }, CancellationToken.None));
        }

        [Test]
        public void WrapAsync_NullHost_ThrowsArgumentNullException()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            using var memoryStream = new System.IO.MemoryStream();

            Assert.ThrowsAsync<System.ArgumentNullException>(async () =>
                await provider.WrapAsync(memoryStream, null, new[] { "h2" }, CancellationToken.None));
        }
    }
}
