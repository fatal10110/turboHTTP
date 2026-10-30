using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// SslStream provider: ALPN is optional when http/1.1 is offered (RFC 7301), the handshake
    /// lets the OS negotiate the version (SslProtocols.None) and TLS below 1.2 is rejected.
    /// </summary>
    [TestFixture]
    public class SslStreamTlsProviderFailClosedTests
    {
        private static readonly string[] H2AndHttp11 = { "h2", "http/1.1" };

        // TLS 1.3 without the SslProtocols.Tls13 member (absent from older target profiles).
        private static SslProtocols Tls13Value => (SslProtocols)((int)SslProtocols.Tls12 << 2);

        [Test]
        public void HandshakeProtocols_IsOsNegotiatedNone()
        {
            Assert.AreEqual(SslProtocols.None, SslStreamTlsProvider.HandshakeProtocols);
        }

        [Test]
        public void EnsureMinimumTlsVersion_AcceptsTls12AndTls13()
        {
            Assert.DoesNotThrow(() => SslStreamTlsProvider.EnsureMinimumTlsVersion(SslProtocols.Tls12));
            Assert.DoesNotThrow(() => SslStreamTlsProvider.EnsureMinimumTlsVersion(Tls13Value));
        }

#pragma warning disable SYSLIB0039, CS0618 // obsolete protocol members are the point of the test
        [TestCase(SslProtocols.None)]
        [TestCase(SslProtocols.Ssl3)]
        [TestCase(SslProtocols.Tls)]
        [TestCase(SslProtocols.Tls11)]
        public void EnsureMinimumTlsVersion_RejectsBelowTls12(SslProtocols negotiated)
        {
            Assert.Throws<AuthenticationException>(
                () => SslStreamTlsProvider.EnsureMinimumTlsVersion(negotiated));
        }
#pragma warning restore SYSLIB0039, CS0618

        [Test]
        public void FormatTlsVersion_LabelsTls12AndTls13()
        {
            Assert.AreEqual("1.2", SslStreamTlsProvider.FormatTlsVersion(SslProtocols.Tls12));
            Assert.AreEqual("1.3", SslStreamTlsProvider.FormatTlsVersion(Tls13Value));
        }

        [Test]
        public void ShouldNegotiateAlpn_NoAlpnRuntime_WithHttp11Offered_PlainHandshake()
        {
            Assert.IsFalse(SslStreamTlsProvider.ShouldNegotiateAlpn(H2AndHttp11, alpnSupported: false));
            Assert.IsFalse(SslStreamTlsProvider.ShouldNegotiateAlpn(new[] { "http/1.1" }, alpnSupported: false));
        }

        [Test]
        public void ShouldNegotiateAlpn_NoAlpnRuntime_H2Only_Throws()
        {
            Assert.Throws<PlatformNotSupportedException>(
                () => SslStreamTlsProvider.ShouldNegotiateAlpn(new[] { "h2" }, alpnSupported: false));
        }

        [Test]
        public void ShouldNegotiateAlpn_AlpnRuntime_UsesAlpnWhenProtocolsOffered()
        {
            Assert.IsTrue(SslStreamTlsProvider.ShouldNegotiateAlpn(H2AndHttp11, alpnSupported: true));
            Assert.IsFalse(SslStreamTlsProvider.ShouldNegotiateAlpn(Array.Empty<string>(), alpnSupported: true));
            Assert.IsFalse(SslStreamTlsProvider.ShouldNegotiateAlpn(null, alpnSupported: true));
        }

        [Test]
        public void SimulatedNoAlpn_ReportsAlpnUnsupported()
        {
            Assert.IsFalse(new SslStreamTlsProvider(simulateNoAlpn: true).IsAlpnSupported());
        }

        [Test]
        public void WrapAsync_SimulatedNoAlpn_H2Only_ThrowsPlatformNotSupported()
        {
            var provider = new SslStreamTlsProvider(simulateNoAlpn: true);
            Exception failure = CaptureFailure(() =>
                provider.WrapAsync(new MemoryStream(), "localhost", new[] { "h2" }, CancellationToken.None));

            Assert.IsInstanceOf<PlatformNotSupportedException>(failure);
        }

        [Test]
        public void WrapAsync_SimulatedNoAlpn_H2AndHttp11_PerformsPlainHandshake()
        {
            using var server = LocalTlsTestServer.TryStart();
            if (server == null)
                Assert.Ignore("Local self-signed certificate generation is unavailable in this runtime.");

            var provider = new SslStreamTlsProvider(simulateNoAlpn: true);
            Exception failure = CaptureFailure(async () =>
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, server.Port).ConfigureAwait(false);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var result = await provider.WrapAsync(
                    client.GetStream(), "localhost", H2AndHttp11, cts.Token).ConfigureAwait(false);
                result.SecureStream.Dispose();
            });

            // The plain handshake runs and reaches certificate validation, which rejects the
            // self-signed certificate. Before the fix the provider threw PlatformNotSupportedException
            // without attempting a handshake.
            Assert.IsNotNull(failure, "The self-signed certificate must be rejected.");
            Assert.IsNotInstanceOf<PlatformNotSupportedException>(failure);
            Assert.IsFalse(TlsProviderSelector.IsPlatformTlsException(failure),
                "Expected a handshake/certificate failure, got " + failure.GetType().Name);
        }

        private static Exception CaptureFailure(Func<Task> action)
        {
            try
            {
                Task.Run(action).GetAwaiter().GetResult();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }
}
