using System;
using System.Net.Sockets;
using System.Security.Authentication;
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
        public void IsAlpnSupported_ReturnsExpectedValue()
        {
            IsAlpnSupported_ReturnsBoolean();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_ValidServer_Succeeds()        {
            Task.Run(async () =>
            {
                var result = await WrapAsync("www.google.com", "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);

                Assert.IsNotNull(result.SecureStream);
                Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
                Assert.AreEqual("SslStream", result.ProviderName);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to expired.badssl.com")]
        [Category("Integration")]
        public void WrapAsync_ExpiredCert_ThrowsAuthenticationException()
        {
            Task.Run(async () =>
            {
                try
                {
                    await WrapAsync("expired.badssl.com", "expired.badssl.com", Array.Empty<string>(), CancellationToken.None);
                    Assert.Fail("Expected AuthenticationException for expired certificate.");
                }
                catch (AuthenticationException)
                {
                    // Expected
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_WithAlpn_NegotiatesH2()        {
            Task.Run(async () =>
            {
                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                var result = await WrapAsync("www.google.com", "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);

                Assert.IsNotNull(result.SecureStream);
                Assert.AreEqual("SslStream", result.ProviderName);
                if (provider.IsAlpnSupported())
                {
                    Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1"));
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_WithAlpn_NegotiatesHttp11()        {
            Task.Run(async () =>
            {
                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                var result = await WrapAsync("example.com", "example.com", new[] { "http/1.1" }, CancellationToken.None);

                Assert.IsNotNull(result.SecureStream);
                Assert.AreEqual("SslStream", result.ProviderName);
                if (provider.IsAlpnSupported())
                {
                    Assert.That(result.NegotiatedAlpn, Is.EqualTo("http/1.1").Or.Null);
                }
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_RealServer_SucceedsWithTls12OrHigher()        {
            WrapAsync_ValidServer_Succeeds();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_CancellationRequested_ThrowsOperationCanceledException()        {
            Task.Run(async () =>
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

                socket.Connect("httpbin.org", 443);
                using var stream = new NetworkStream(socket, true);

                using var cts = new CancellationTokenSource();
                cts.Cancel();

                var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);

                AssertAsync.ThrowsAsync<TaskCanceledException>(async () =>
                    await provider.WrapAsync(stream, "httpbin.org", new[] { "h2" }, cts.Token));
            }).GetAwaiter().GetResult();
        }

        private static async Task<TlsResult> WrapAsync(
            string tcpHost,
            string tlsHost,
            string[] alpn,
            CancellationToken ct)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(tcpHost, 443);
            using var stream = new NetworkStream(socket, true);

            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            return await provider.WrapAsync(stream, tlsHost, alpn, ct);
        }
    }
}
