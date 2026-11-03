using System;
using System.IO;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;
using Certs = TurboHTTP.Tests.Transport.Tls.NegativeSuiteTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #113: the negative TLS suite. Every bad server identity is rejected, for every provider
    /// (<see cref="TlsBackend.SslStream"/>, <see cref="TlsBackend.BouncyCastle"/>,
    /// <see cref="TlsBackend.Auto"/>), as <see cref="UHttpErrorType.CertificateError"/> through the
    /// transport: self-signed, untrusted root, expired, not yet valid, wrong host, CN-only identity
    /// when a SAN is present, an IP host without an iPAddress SAN, a missing intermediate, a
    /// <c>*.com</c> wildcard, and a server below TLS 1.2. Auto never falls back to BouncyCastle on
    /// a certificate failure (#34). A missing BouncyCastle provider fails the BouncyCastle cases
    /// (#140), it never skips them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The loopback server presents the committed leaves of <see cref="NegativeSuiteTestCertificates"/>
    /// (one test root; one shared key). BouncyCastle trusts that root through the test-only anchor
    /// (<c>ServerCertificateVerifier.AddTestTrustAnchor</c>), so each BouncyCastle case fails for
    /// exactly its own defect: <see cref="BouncyCastle_ValidLeaf_Succeeds"/> is the positive control
    /// with the same root and key. Platform <c>SslStream</c> (and Auto) cannot be made to trust a
    /// test root from a Unity test, so here its cases are rejected at least as untrusted; the
    /// per-defect SslStream check, with the root trusted and positive controls, is the
    /// <c>Tools~/tls-interop</c> job against <c>openssl s_server</c> (#230), which runs in PR CI.
    /// </para>
    /// <para>
    /// Mutation check (#113): making a provider accept every certificate makes its cases here fail.
    /// BouncyCastle: <c>TurboTlsAuthentication.NotifyServerCertificate</c> returning before
    /// <c>ValidateLeaf</c> and <c>ServerCertificateVerifier.Verify</c>. SslStream and Auto:
    /// <c>SslStreamTlsProvider.ValidateServerCertificate</c> returning <c>true</c>. Exceptions: the
    /// BouncyCastle <see cref="ServerBelowTls12_IsCertificateError"/> case, a protocol verdict that
    /// no certificate hook changes (Unity's SslStream continues a TLS 1.1 handshake to the
    /// certificate, so its case does fail under the mutation), and the SslStream/Auto
    /// <c>ip-host-without-ip-san</c> case, which the provider's own post-handshake check
    /// (<c>SubjectAltNameIpAddresses</c>) also rejects. The check was performed for #113 and is recorded in
    /// <c>Development~/docs/implementation-journal/2026-11-tls-interop-suite.md</c>; repeat it when
    /// this suite or a provider's validation changes.
    /// </para>
    /// </remarks>
    [TestFixture]
    public class TlsNegativeSuiteTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(new[] { Certs.Root });
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
            TlsProviderSelector.ResetProbeState();
        }

        [Test]
        public void BadServerIdentity_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend,
            [Values("self-signed", "untrusted-root", "expired", "not-yet-valid", "wrong-host",
                "cn-only-with-san", "ip-host-without-ip-san", "missing-intermediate")] string identity)
        {
            RequireProvider(backend);
            string pfx;
            string host = "localhost";
            switch (identity)
            {
                case "self-signed": pfx = Certs.SelfSigned; break;
                case "untrusted-root": pfx = Certs.UntrustedRoot; break;
                case "expired": pfx = Certs.Expired; break;
                case "not-yet-valid": pfx = Certs.NotYetValid; break;
                case "wrong-host": pfx = Certs.WrongHost; break;
                case "cn-only-with-san": pfx = Certs.CnOnlyWithSan; break;
                case "ip-host-without-ip-san": pfx = Certs.IpInDnsSan; host = "127.0.0.1"; break;
                case "missing-intermediate": pfx = Certs.ViaIntermediate; break;
                default: throw new ArgumentOutOfRangeException(nameof(identity), identity, null);
            }

            using var server = StartServer(pfx);
            var ex = AssertAsync.ThrowsAsync<UHttpException>(() => SendAsync(backend, server.Port, host));

            Assert.AreEqual(UHttpErrorType.CertificateError, ex.HttpError.Type, ex.ToString());
            Assert.AreEqual(1, server.AcceptedConnections, "One handshake, no retry on another provider.");
        }

        [Test]
        public void TldWildcard_RejectedForExampleCom(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            // The transport resolves the request host, so this case drives the provider directly with
            // host example.com over a loopback socket; AuthenticationException is exactly what
            // RawSocketTransport maps to CertificateError (BadServerIdentity_IsCertificateError).
            var provider = RequireProvider(backend);
            using var server = StartServer(Certs.TldWildcard);

            AssertAsync.ThrowsAsync<AuthenticationException>(async () =>
            {
                var stream = await BouncyCastleLoopbackServer.ConnectAsync(server.Port).ConfigureAwait(false);
                TlsResult result;
                try
                {
                    using var cts = new CancellationTokenSource(Bound);
                    result = await provider.WrapAsync(stream, "example.com", new[] { "http/1.1" }, cts.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

                result.SecureStream.Dispose();
            });
        }

        [Test]
        public void ServerBelowTls12_IsCertificateError(
            [Values(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.Auto)] TlsBackend backend)
        {
            RequireProvider(backend);
            byte[] leafDer;
            using (var leaf = Certs.TryLoad(Certs.Valid))
            {
                if (leaf == null)
                {
                    TestRequirements.IgnoreEnvironment("The committed negative-suite PKCS#12 identities could not be loaded in this runtime.");
                    return;
                }

                leafDer = leaf.RawData;
            }

            using var server = BouncyCastleLoopbackServer.StartRaw(stream => AnswerAsTls11Server(stream, leafDer));

            var ex = AssertAsync.ThrowsAsync<UHttpException>(() => SendAsync(backend, server.Port, "localhost"));

            Assert.AreEqual(UHttpErrorType.CertificateError, ex.HttpError.Type, ex.ToString());
            Assert.AreEqual(1, server.AcceptedConnections, "One handshake, no retry on another provider.");
        }

        [Test]
        public void Auto_CertificateFailure_NeverFallsBackToBouncyCastle()
        {
            using var server = StartServer(Certs.SelfSigned);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(() => SendAsync(TlsBackend.Auto, server.Port, "localhost"));

            Assert.AreEqual(UHttpErrorType.CertificateError, ex.HttpError.Type, ex.ToString());
            Assert.AreEqual(1, server.AcceptedConnections, "A BouncyCastle fallback would open a second connection.");
            Assert.IsFalse(TlsProviderSelector.IsSslStreamKnownBroken(), "A certificate failure is not a platform failure.");
            Assert.AreEqual("SslStream", TlsProviderSelector.GetProvider(TlsBackend.Auto).ProviderName);
        }

        [Test]
        public void BouncyCastle_ValidLeaf_Succeeds([Values("localhost", "127.0.0.1")] string host)
        {
            RequireProvider(TlsBackend.BouncyCastle);
            using var server = StartServer(Certs.Valid);

            AssertAsync.Run(() => SendAsync(TlsBackend.BouncyCastle, server.Port, host));
            Assert.IsNull(server.HandlerError);
        }

        private static ITlsProvider RequireProvider(TlsBackend backend)
        {
            return backend == TlsBackend.BouncyCastle
                ? TestRequirements.RequireBouncyCastleProvider()
                : TlsProviderSelector.GetProvider(backend);
        }

        private static BouncyCastleLoopbackServer StartServer(string pfx)
        {
            var certificate = Certs.TryLoad(pfx);
            if (certificate == null)
                TestRequirements.IgnoreEnvironment("The committed negative-suite PKCS#12 identities could not be loaded in this runtime.");
            return BouncyCastleLoopbackServer.StartTls(certificate, RespondOk);
        }

        private static async Task SendAsync(TlsBackend backend, int port, string host)
        {
            using var pool = new TcpConnectionPool(tlsBackend: backend);
            using var transport = new RawSocketTransport(pool, backend);
            var pipeline = new TestInterceptorPipeline(Array.Empty<IHttpInterceptor>(), transport);
            var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://{host}:{port}/")).WithTimeout(Bound);
            using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        }

        /// <summary>Reads the request head, answers 200 with an empty body, closes.</summary>
        private static void RespondOk(Stream stream)
        {
            var window = new byte[4];
            var one = new byte[1];
            int seen = 0;
            while (seen < 16 * 1024)
            {
                if (stream.Read(one, 0, 1) != 1)
                    return;
                Buffer.BlockCopy(window, 1, window, 0, 3);
                window[3] = one[0];
                seen++;
                if (window[0] == '\r' && window[1] == '\n' && window[2] == '\r' && window[3] == '\n')
                    break;
            }

            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            stream.Write(response, 0, response.Length);
            stream.Flush();
        }

        /// <summary>
        /// Reads the ClientHello record and answers as a server that cannot speak TLS 1.2: one TLS 1.1
        /// record (version 0x0302) with ServerHello (legacy version 0x0302, no supported_versions,
        /// TLS_RSA_WITH_AES_128_CBC_SHA), Certificate and ServerHelloDone. RFC 8996 and RFC 8446
        /// 4.2.1: a client that offers only TLS 1.2 and 1.3 aborts with protocol_version at the
        /// ServerHello (BouncyCastle does). A platform SslStream that still accepts TLS 1.1 goes on
        /// to the certificate, which it does not trust, and the provider's TLS 1.2 floor
        /// (<c>EnsureMinimumTlsVersion</c>) stands behind that; either way the handshake fails as a
        /// certificate error, without the server having to finish a TLS 1.1 handshake.
        /// </summary>
        private static void AnswerAsTls11Server(Stream stream, byte[] leafDer)
        {
            var header = new byte[5];
            BouncyCastleLoopbackServer.ReadExact(stream, header, 5);
            int length = (header[3] << 8) | header[4];
            BouncyCastleLoopbackServer.ReadExact(stream, new byte[length], length);

            var flight = new MemoryStream();
            // ServerHello: type 2, length 38.
            flight.Write(new byte[] { 0x02, 0x00, 0x00, 38, 0x03, 0x02 }, 0, 6);
            for (int r = 0; r < 32; r++)
                flight.WriteByte((byte)(0x40 + r));
            flight.Write(new byte[] { 0x00, 0x00, 0x2F, 0x00 }, 0, 4); // no session id, suite 0x002F, null compression
            // Certificate: type 11, certificate_list with one entry (24-bit lengths).
            int entry = 3 + leafDer.Length;
            WriteHandshakeHeader(flight, 0x0B, 3 + entry);
            WriteUInt24(flight, entry);
            WriteUInt24(flight, leafDer.Length);
            flight.Write(leafDer, 0, leafDer.Length);
            // ServerHelloDone: type 14, empty.
            flight.Write(new byte[] { 0x0E, 0x00, 0x00, 0x00 }, 0, 4);

            var body = flight.ToArray();
            var record = new byte[5 + body.Length];
            record[0] = 0x16;
            record[1] = 0x03;
            record[2] = 0x02;
            record[3] = (byte)(body.Length >> 8);
            record[4] = (byte)body.Length;
            Buffer.BlockCopy(body, 0, record, 5, body.Length);
            stream.Write(record, 0, record.Length);
            stream.Flush();
            BouncyCastleLoopbackServer.DrainUntilClosed(stream);
        }

        private static void WriteHandshakeHeader(Stream stream, byte type, int length)
        {
            stream.WriteByte(type);
            WriteUInt24(stream, length);
        }

        private static void WriteUInt24(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }
}
