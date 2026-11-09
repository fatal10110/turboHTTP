using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #418: the SslStream provider reports ALPN only where the runtime can negotiate it. Unity's
    /// Mono class library (Editor and IL2CPP players) builds the ALPN options but ignores them, and
    /// reading <c>NegotiatedApplicationProtocol</c> throws PlatformNotSupportedException
    /// (mono#12880): there the provider reports no ALPN, and without BouncyCastle the transport
    /// reports no HTTP/2 (RFC 9113 section 3.2: h2 over TLS only through ALPN).
    /// </summary>
    [TestFixture]
    public class SslStreamAlpnTruthTests
    {
        [TearDown]
        public void TearDown()
        {
            // Leave the registration play-mode entry produced.
            HttpTransportFactory.DetachCreatedDefault()?.Dispose();
            HttpTransportFactory.Reset();
            TurboHttpBootstrap.RegisterDefaultTransports();
            TlsPlatformCapabilities.RefreshForTesting();
        }

        [Test]
        public void ReportsNegotiatedProtocol_PlatformNotSupported_MeansNoAlpn()
        {
            Assert.IsFalse(SslStreamTlsProvider.ReportsNegotiatedProtocol(
                () => throw new PlatformNotSupportedException("https://github.com/mono/mono/issues/12880")));
        }

        [Test]
        public void ReportsNegotiatedProtocol_NotAuthenticatedYet_MeansAlpn()
        {
            Assert.IsTrue(SslStreamTlsProvider.ReportsNegotiatedProtocol(
                () => throw new InvalidOperationException("This operation is only allowed using a successfully authenticated context.")));
        }

        [Test]
        public void ReportsNegotiatedProtocol_ValueRead_MeansAlpn()
        {
            Assert.IsTrue(SslStreamTlsProvider.ReportsNegotiatedProtocol(() => { }));
        }

        [Test]
        public void ReportsNegotiatedProtocol_OtherFailure_MeansNoAlpn()
        {
            Assert.IsFalse(SslStreamTlsProvider.ReportsNegotiatedProtocol(
                () => throw new NotImplementedException()));
        }

        [Test]
        public void IsAlpnSupported_MatchesWhetherThisRuntimeReportsTheAlpnResult()
        {
            // Unity's Editor and players: false (the read throws PNSE). A runtime with ALPN: true.
            Assert.AreEqual(AlpnAssert.SslStreamReportsAlpn, SslStreamTlsProvider.Instance.IsAlpnSupported(),
                "SslStream provider ALPN report vs. this runtime's NegotiatedApplicationProtocol");
        }

        [Test]
        public void TlsPlatformCapabilities_SystemAlpn_FollowsTheRuntime()
        {
            TlsProviderSelector.ResetProbeState();
            TlsPlatformCapabilities.RefreshForTesting();

            var summary = TlsPlatformCapabilities.GetSummary();
            if (!summary.IsSystemTlsAvailable)
                TestRequirements.IgnoreEnvironment("No SslStream on this runtime.");

            Assert.AreEqual(AlpnAssert.SslStreamReportsAlpn, summary.IsSystemAlpnExpected,
                "SystemAlpnExpected vs. this runtime's NegotiatedApplicationProtocol");
        }

        [Test]
        public void TransportCapabilities_WithoutBouncyCastle_Http2OnlyWhereTheRuntimeNegotiatesAlpn()
        {
            // The test build compiles BouncyCastle in, so the registered capabilities always say
            // Http2 here; the rule is checked for a build without it.
            var expected = AlpnAssert.SslStreamReportsAlpn ? TransportCapabilities.Http2 : TransportCapabilities.None;
            Assert.AreEqual(expected,
                RawSocketTransport.CapabilitiesFor(SslStreamTlsProvider.Instance.IsAlpnSupported(), bouncyCastleAvailable: false),
                "Without BouncyCastle, HTTP/2 needs SslStream ALPN on this runtime (RFC 9113 section 3.2)");
        }

        [Test]
        public void TransportCapabilities_WithBouncyCastle_ReportsHttp2()
        {
            Assert.AreEqual(TransportCapabilities.Http2,
                RawSocketTransport.CapabilitiesFor(sslStreamAlpn: false, bouncyCastleAvailable: true));
        }

        [Test]
        public void RawSocketTransport_EnsureRegistered_ReportsTheSameRule()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();

            var expected = RawSocketTransport.CapabilitiesFor(
                AlpnAssert.SslStreamReportsAlpn, TlsProviderSelector.IsBouncyCastleAvailable());
            Assert.That(HttpTransportFactory.Capabilities, Is.EqualTo(expected));
            Assert.That(PlatformConfig.SupportsHttp2, Is.EqualTo(expected == TransportCapabilities.Http2));
        }
    }
}
