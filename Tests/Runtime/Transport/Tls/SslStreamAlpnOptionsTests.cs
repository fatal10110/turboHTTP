using System;
using System.Net.Security;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// SslStream ALPN options are built through the .NET Standard 2.1 API without reflection
    /// (#303), and a runtime that cannot build them (missing AOT code or metadata) falls back to a
    /// handshake without ALPN when http/1.1 is offered (RFC 7301 section 3.1).
    /// </summary>
    [TestFixture]
    public class SslStreamAlpnOptionsTests
    {
        [Test]
        public void BuildAlpnOptions_H2AndHttp11_ListsBothInOfferOrder()
        {
            SslClientAuthenticationOptions options =
                SslStreamTlsProvider.BuildAlpnOptions("example.com", AlpnProtocols.H2AndHttp11);

            Assert.IsNotNull(options.ApplicationProtocols);
            CollectionAssert.AreEqual(
                new[] { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                options.ApplicationProtocols);
            Assert.AreEqual("example.com", options.TargetHost);
            Assert.AreEqual(SslStreamTlsProvider.HandshakeProtocols, options.EnabledSslProtocols);
            Assert.IsNull(options.RemoteCertificateValidationCallback,
                "Validation stays with the SslStream constructor callback.");
        }

        [Test]
        public void BuildAlpnOptions_SkipsUnknownProtocols_FreshListPerCall()
        {
            var first = SslStreamTlsProvider.BuildAlpnOptions("example.com", new[] { "spdy/3", "http/1.1" });
            var second = SslStreamTlsProvider.BuildAlpnOptions("example.com", new[] { "http/1.1" });

            CollectionAssert.AreEqual(new[] { SslApplicationProtocol.Http11 }, first.ApplicationProtocols);
            Assert.AreNotSame(first.ApplicationProtocols, second.ApplicationProtocols);
        }

        [Test]
        public void IsAlpnSetupFailure_AcceptsMissingAotCodeExceptions()
        {
#pragma warning disable CS0618 // ExecutionEngineException is what IL2CPP throws for missing AOT code.
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new ExecutionEngineException()));
#pragma warning restore CS0618
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new NotSupportedException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new PlatformNotSupportedException()));
            Assert.IsTrue(SslStreamTlsProvider.IsAlpnSetupFailure(new NotImplementedException()));
            Assert.IsFalse(SslStreamTlsProvider.IsAlpnSetupFailure(new InvalidOperationException()));
        }

        [Test]
        public void ResolveAlpnOptions_BuilderThrowsExecutionEngine_Http11Offered_ReturnsNullForPlainHandshake()
        {
#pragma warning disable CS0618
            object options = SslStreamTlsProvider.ResolveAlpnOptions(
                (host, protocols) => throw new ExecutionEngineException("no AOT code"),
                "example.com", AlpnProtocols.H2AndHttp11, http11Offered: true);
#pragma warning restore CS0618

            Assert.IsNull(options, "Null selects the handshake without ALPN (HTTP/1.1).");
        }

        [Test]
        public void ResolveAlpnOptions_BuilderThrowsNotSupported_H2Only_ThrowsPlatformNotSupported()
        {
            var cause = new NotSupportedException("no AOT code");

            var ex = Assert.Throws<PlatformNotSupportedException>(() => SslStreamTlsProvider.ResolveAlpnOptions(
                (host, protocols) => throw cause,
                "example.com", new[] { "h2" }, http11Offered: false));

            Assert.AreSame(cause, ex.InnerException);
        }

        [Test]
        public void ResolveAlpnOptions_BuilderThrowsInvalidOperation_Propagates()
        {
            var stateError = new InvalidOperationException("not a capability failure");

            var ex = Assert.Throws<InvalidOperationException>(() => SslStreamTlsProvider.ResolveAlpnOptions(
                (host, protocols) => throw stateError,
                "example.com", AlpnProtocols.H2AndHttp11, http11Offered: true));

            Assert.AreSame(stateError, ex);
        }

        [Test]
        public void ResolveAlpnOptions_RealBuilder_ReturnsOptionsWithBothProtocols()
        {
            object options = SslStreamTlsProvider.ResolveAlpnOptions(
                SslStreamTlsProvider.BuildAlpnOptions, "example.com", AlpnProtocols.H2AndHttp11, http11Offered: true);

            Assert.IsInstanceOf<SslClientAuthenticationOptions>(options);
            CollectionAssert.AreEqual(
                new[] { SslApplicationProtocol.Http2, SslApplicationProtocol.Http11 },
                ((SslClientAuthenticationOptions)options).ApplicationProtocols);
        }

        [Test]
        public void IsAlpnSupported_ProbeBuildsOptionsOnThisRuntime()
        {
            // The type initializer probes the same builder (it succeeds wherever the builder does)
            // and whether the runtime reports the result: Unity's runtimes do not (#418).
            Assert.AreEqual(AlpnAssert.SslStreamReportsAlpn, SslStreamTlsProvider.Instance.IsAlpnSupported());
        }
    }
}
