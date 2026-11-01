using System;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;
using Certs = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #33: the BouncyCastle provider validates the server certificate: chain to a trusted anchor
    /// (<see cref="ServerCertificateVerifier"/>), RFC 6125 host identity, serverAuth EKU, validity.
    /// Leaf checks run on committed certificates through <c>TurboTlsAuthentication.ValidateLeaf</c>;
    /// the chain and host cases also run end to end against the loopback TLS server, whose
    /// self-signed certificate is trusted only through the test anchor.
    /// </summary>
    [TestFixture]
    public class BouncyCastleChainValidationTests
    {
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        private static void AssertLeafAccepted(byte[] der, string host)
        {
            Assert.DoesNotThrow(() => TlsHostnameValidationTests.InvokeValidateLeaf(der, host, Now));
        }

        private static void AssertLeafRejected(byte[] der, string host, string alert, DateTime? now = null)
        {
            var message = TlsHostnameValidationTests.AssertTlsFatalAlert(
                () => TlsHostnameValidationTests.InvokeValidateLeaf(der, host, now ?? Now));
            StringAssert.Contains(alert, message);
        }

        [Test]
        public void Leaf_TldWildcard_RejectedForExampleCom()
        {
            AssertLeafRejected(Certs.LeafTldWildcard, "example.com", "bad_certificate");
        }

        [Test]
        public void Leaf_IdnHost_MatchesALabelSan()
        {
            AssertLeafAccepted(Certs.LeafIdn, "bücher.example");
            AssertLeafAccepted(Certs.LeafIdn, "xn--bcher-kva.example");
            AssertLeafRejected(Certs.LeafIdn, "bucher.example", "bad_certificate");
        }

        [Test]
        public void Leaf_IpAndTrailingDotHosts_Match()
        {
            AssertLeafAccepted(Certs.LeafLocalhost, "[::1]");
            AssertLeafAccepted(Certs.LeafLocalhost, "127.0.0.1");
            AssertLeafAccepted(Certs.LeafLocalhost, "localhost.");
            AssertLeafRejected(Certs.LeafLocalhost, "127.0.0.2", "bad_certificate");
            AssertLeafRejected(Certs.LeafLocalhost, "[::2]", "bad_certificate");
        }

        [Test]
        public void Leaf_CommonName_OnlyWithoutSubjectAltName()
        {
            AssertLeafAccepted(Certs.LeafCnOnly, "example.com");
            AssertLeafRejected(Certs.LeafCnWithSan, "example.com", "bad_certificate");
            AssertLeafAccepted(Certs.LeafCnWithSan, "other.example");
        }

        [Test]
        public void Leaf_WrongHost_Rejected()
        {
            AssertLeafRejected(Certs.LeafLocalhost, "example.com", "bad_certificate");
        }

        [Test]
        public void Leaf_ClientAuthOnlyEku_Rejected()
        {
            AssertLeafRejected(Certs.LeafClientAuthOnly, "localhost", "bad_certificate");
        }

        [Test]
        public void Leaf_ExpiredAndNotYetValid_Rejected()
        {
            AssertLeafRejected(Certs.LeafExpired, "localhost", "certificate_expired");
            AssertLeafRejected(Certs.LeafNotYetValid, "localhost", "certificate_expired");
        }

        [Test]
        public void Loopback_SelfSignedWithoutAnchor_FailsWithAuthenticationException()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = StartServer();
            if (server == null)
                return;

            // Starting the server registered the test anchor; the production configuration has none.
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);

            var ex = AssertAsync.ThrowsAsync<AuthenticationException>(() => HandshakeAsync(provider, server.Port, "localhost"));
            StringAssert.Contains("unknown_ca", ex.ToString());
        }

        [Test]
        public void Loopback_SelfSignedWithoutAnchor_IsCertificateErrorThroughTransport()
        {
            BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = StartServer();
            if (server == null)
                return;

            ServerCertificateVerifier.RestoreTestTrustAnchors(null);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle);
                var pipeline = new TestInterceptorPipeline(Array.Empty<IHttpInterceptor>(), transport);
                var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://localhost:{server.Port}/"))
                    .WithTimeout(TimeSpan.FromSeconds(30));
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            });

            Assert.AreEqual(UHttpErrorType.CertificateError, ex.HttpError.Type, ex.Message);
        }

        [Test]
        public void Loopback_WrongHost_FailsWithAuthenticationException()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = StartServer();
            if (server == null)
                return;

            var ex = AssertAsync.ThrowsAsync<AuthenticationException>(() => HandshakeAsync(provider, server.Port, "wrong.example"));
            StringAssert.Contains("bad_certificate", ex.ToString());
        }

        [TestCase("localhost")]
        [TestCase("localhost.")]
        [TestCase("127.0.0.1")]
        [TestCase("[::1]")]
        public void Loopback_AnchoredCertificate_MatchingHost_Succeeds(string host)
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = StartServer();
            if (server == null)
                return;

            AssertAsync.Run(() => HandshakeAsync(provider, server.Port, host));
        }

        private static BouncyCastleLoopbackServer StartServer()
        {
            var server = BouncyCastleLoopbackServer.TryStartTls(BouncyCastleLoopbackServer.DrainUntilClosed);
            if (server == null)
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
            return server;
        }

        private static async Task HandshakeAsync(ITlsProvider provider, int port, string host)
        {
            var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            TlsResult result;
            try
            {
                using var cts = new CancellationTokenSource(Bound);
                result = await provider.WrapAsync(stream, host, Array.Empty<string>(), cts.Token).ConfigureAwait(false);
            }
            catch
            {
                stream.Dispose();
                throw;
            }

            using (Stream secure = result.SecureStream)
            {
                Assert.IsNotNull(secure);
            }
        }
    }
}
