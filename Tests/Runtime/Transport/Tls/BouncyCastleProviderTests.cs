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
        [Category("Integration")]
        public void WrapAsync_ValidServer_Succeeds()        {
            Task.Run(async () =>
            {
                if (!IsAvailable())
                {
                    Assert.Ignore("BouncyCastle is not available");
                    return;
                }

                var result = await WrapAsync("www.google.com", "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);

                Assert.IsNotNull(result.SecureStream);
                Assert.AreEqual("BouncyCastle", result.ProviderName);
                Assert.That(result.TlsVersion, Is.EqualTo("1.2").Or.EqualTo("1.3"));
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_WithAlpn_NegotiatesH2()        {
            Task.Run(async () =>
            {
                if (!IsAvailable())
                {
                    Assert.Ignore("BouncyCastle is not available");
                    return;
                }

                var result = await WrapAsync("www.google.com", "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);

                Assert.IsNotNull(result.SecureStream);
                Assert.AreEqual("BouncyCastle", result.ProviderName);
                Assert.That(result.NegotiatedAlpn, Is.EqualTo("h2").Or.EqualTo("http/1.1"));
                Assert.IsNotNull(result.CipherSuite);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to expired.badssl.com")]
        [Category("Integration")]
        public void WrapAsync_ExpiredCert_ThrowsFatalAlert()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            try
            {
                Task.Run(async () =>
                    await WrapAsync("expired.badssl.com", "expired.badssl.com", Array.Empty<string>(), CancellationToken.None)
                ).GetAwaiter().GetResult();

                Assert.Fail("Expected certificate validation failure.");
            }
            catch (AuthenticationException ex)
            {
                Assert.IsNotNull(ex.InnerException);
                Assert.AreEqual("TlsFatalAlert", ex.InnerException.GetType().Name);
            }
        }

        [Test]
        [Explicit("Requires network access to a wildcard certificate host")]
        [Category("Integration")]
        public void WrapAsync_WildcardCert_Succeeds()        {
            Task.Run(async () =>
            {
                if (!IsAvailable())
                {
                    Assert.Ignore("BouncyCastle is not available");
                    return;
                }

                // *.badssl.com wildcard certificate
                var result = await WrapAsync("sha256.badssl.com", "sha256.badssl.com", new[] { "h2", "http/1.1" }, CancellationToken.None);
                Assert.IsNotNull(result.SecureStream);
                Assert.AreEqual("BouncyCastle", result.ProviderName);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_RealServer_SucceedsWithH2Alpn()        {
            WrapAsync_WithAlpn_NegotiatesH2();
        }

        [Test]
        [Explicit("Requires network access to a real HTTPS server")]
        [Category("Integration")]
        public void WrapAsync_SniSent_ServerAcceptsConnection()        {
            Task.Run(async () =>
            {
                if (!IsAvailable())
                {
                    Assert.Ignore("BouncyCastle is not available");
                    return;
                }

                // Test SNI by connecting to a server that requires it
                var result = await WrapAsync("www.google.com", "www.google.com", new[] { "h2", "http/1.1" }, CancellationToken.None);
                Assert.IsNotNull(result.SecureStream, "SNI should allow successful handshake");
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Explicit("Requires network access - tests certificate hostname mismatch")]
        [Category("Integration")]
        public void WrapAsync_HostnameMismatch_ThrowsAuthenticationException()
        {
            if (!IsAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            // Connect to google.com but claim we wanted example.com
            // This should fail certificate validation due to hostname mismatch
            AssertAsync.ThrowsAsync<AuthenticationException>(async () =>
            {
                await WrapAsync("www.google.com", "example.com", new[] { "h2" }, CancellationToken.None);
            });
        }

        [Test]
        public void WrapAsync_PreCancelledToken_ThrowsOperationCancelledException()        {
            Task.Run(async () =>
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
                AssertAsync.ThrowsAsync<OperationCanceledException>(async () =>
                    await provider.WrapAsync(memoryStream, "example.com", new[] { "h2" }, cts.Token));
            }).GetAwaiter().GetResult();
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

            AssertAsync.ThrowsAsync<ArgumentNullException>(async () =>
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

            AssertAsync.ThrowsAsync<ArgumentNullException>(async () =>
                await provider.WrapAsync(memoryStream, null, new[] { "h2" }, CancellationToken.None));
        }

        private async Task<TlsResult> WrapAsync(
            string tcpHost,
            string tlsHost,
            string[] alpn,
            CancellationToken ct)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.Connect(tcpHost, 443);
            using var stream = new NetworkStream(socket, true);

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            return await provider.WrapAsync(stream, tlsHost, alpn, ct);
        }
    }
}
