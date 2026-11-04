using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142: the BouncyCastle ClientHello, captured by a scripted loopback server.
    /// <list type="bullet">
    /// <item>server_name carries the A-label of an IDN host and is absent for IPv4/IPv6 literals
    /// (RFC 6066 section 3).</item>
    /// <item>No status_request / status_request_v2 (RFC 6066 section 8, RFC 6961): nothing validates
    /// a staple until #33.</item>
    /// <item>A server that selects only TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384 (TLS 1.2) or
    /// TLS_AES_256_GCM_SHA384 (TLS 1.3) is accepted, and one that selects a static-RSA suite is
    /// rejected. The exact offered list is pinned by
    /// <see cref="BouncyCastleHostileCertificateTests.ClientHello_OffersExactlyTheStrippedCipherSuites"/>.</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class BouncyCastleClientHelloTests
    {
        private const byte ServerHelloType = 2;

        [TestCase("localhost", "localhost")]
        [TestCase("localhost.", "localhost")]
        [TestCase("b\u00fccher.example", "xn--bcher-kva.example")]
        public void ClientHello_SendsAsciiServerName(string host, string expected)
        {
            var hello = CaptureClientHello(host);

            CollectionAssert.AreEqual(new[] { expected }, ClientHelloCapture.ServerNames(hello),
                ClientHelloCapture.Describe(hello));
        }

        [TestCase("127.0.0.1")]
        [TestCase("::1")]
        [TestCase("[::1]")]
        public void ClientHello_IpLiteral_SendsNoServerName(string host)
        {
            var hello = CaptureClientHello(host);

            Assert.IsNull(ClientHelloCapture.ServerNames(hello),
                "RFC 6066 section 3: literal addresses are not permitted in server_name. " +
                ClientHelloCapture.Describe(hello));
        }

        [Test]
        public void ClientHello_HasNoCertificateStatusRequest()
        {
            var hello = CaptureClientHello("localhost");

            Assert.IsNull(ClientHelloCapture.FindExtension(hello, ClientHelloCapture.StatusRequestExtension),
                "status_request offered. " + ClientHelloCapture.Describe(hello));
            Assert.IsNull(ClientHelloCapture.FindExtension(hello, ClientHelloCapture.StatusRequestV2Extension),
                "status_request_v2 offered. " + ClientHelloCapture.Describe(hello));
        }

        [Test]
        public void Tls12ServerSelectingEcdheRsaAes256Gcm_IsAccepted()
        {
            // TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384. Accepted means the client waits for the
            // server's Certificate (and runs into the handshake bound) instead of sending a fatal
            // alert.
            var ex = RunServerHelloScript(
                hello => BuildTls12ServerHello(0xC030),
                AcceptanceTimeoutMillis);

            AssertHandshakeTimedOut(ex);
        }

        [Test]
        public void Tls13ServerSelectingAes256Gcm_IsAccepted()
        {
            // TLS_AES_256_GCM_SHA384 with an x25519 key share. Accepted means the client derives
            // the handshake keys and waits for EncryptedExtensions.
            var ex = RunServerHelloScript(
                hello =>
                {
                    CollectionAssert.Contains(ClientHelloCapture.KeyShareGroups(hello), X25519,
                        "The client must offer an x25519 key share.");
                    return BuildTls13ServerHello(ClientHelloCapture.SessionId(hello), 0x1302);
                },
                AcceptanceTimeoutMillis);

            AssertHandshakeTimedOut(ex);
        }

        [Test]
        public void Tls12ServerSelectingStaticRsaSuite_IsRejected()
        {
            // TLS_RSA_WITH_AES_256_GCM_SHA384 is not offered: the client must abort (RFC 8446
            // section 4.1.3 / RFC 5246 section 7.4.1.3), which shows the acceptance tests above
            // discriminate.
            var ex = RunServerHelloScript(
                hello => BuildTls12ServerHello(0x009D),
                AcceptanceTimeoutMillis);

            Assert.IsInstanceOf<AuthenticationException>(ex, ex?.ToString());
            StringAssert.Contains("illegal_parameter", ex.Message);
        }

        private const int AcceptanceTimeoutMillis = 2000;
        private const int X25519 = 0x001D;

        private static byte[] CaptureClientHello(string host)
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            byte[] clientHello = null;
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                Volatile.Write(ref clientHello, ClientHelloCapture.ReadHandshakeMessage(stream));
                ClientHelloCapture.WriteHandshakeFailureAlert(stream);
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            AssertAsync.ThrowsAsync<AuthenticationException>(
                async () => await HandshakeAsync(provider, server.Port, host));

            var captured = Volatile.Read(ref clientHello);
            Assert.IsNotNull(captured, "The server did not capture a ClientHello: " + server.HandlerError);
            return captured;
        }

        /// <summary>
        /// Reads the ClientHello, answers with <paramref name="serverHello"/> and keeps the
        /// connection open until the client closes it. Returns the client's handshake failure.
        /// </summary>
        private static Exception RunServerHelloScript(Func<byte[], byte[]> serverHello, int handshakeTimeoutMillis)
        {
            var provider = ClientHelloCapture.CreateBouncyCastleProvider(handshakeTimeoutMillis);
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                var hello = ClientHelloCapture.ReadHandshakeMessage(stream);
                WriteHandshakeRecord(stream, ServerHelloType, serverHello(hello));
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            Exception failure = null;
            AssertAsync.Run(async () =>
            {
                try
                {
                    var result = await HandshakeAsync(provider, server.Port, "localhost");
                    result.SecureStream.Dispose();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            });

            Assert.IsNull(server.HandlerError, "Scripted server failed: " + server.HandlerError);
            Assert.IsNotNull(failure, "The scripted handshake cannot complete.");
            return failure;
        }

        private static void AssertHandshakeTimedOut(Exception ex)
        {
            Assert.IsInstanceOf<IOException>(ex, "Expected the handshake bound, got: " + ex);
            Assert.IsInstanceOf<TimeoutException>(ex.InnerException,
                "The client rejected the ServerHello instead of waiting for the next message: " + ex);
        }

        private static async Task<TlsResult> HandshakeAsync(ITlsProvider provider, int port, string host)
        {
            // The provider disposes the stream on failure; on success the caller disposes the result.
            var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await provider.WrapAsync(stream, host, new[] { "http/1.1" }, cts.Token)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// TLS 1.2 ServerHello (no supported_versions, RFC 8446 section 4.1.3) with an empty
        /// renegotiation_info (RFC 5746).
        /// </summary>
        private static byte[] BuildTls12ServerHello(int cipherSuite)
        {
            var body = new MemoryStream();
            WriteServerHelloPrefix(body, Array.Empty<byte>(), cipherSuite);
            WriteExtensions(body, new byte[] { 0xFF, 0x01, 0x00, 0x01, 0x00 });
            return body.ToArray();
        }

        /// <summary>
        /// TLS 1.3 ServerHello (RFC 8446 section 4.1.3): supported_versions = 0x0304 and an x25519
        /// key share. The share is the curve's base point (u = 9), a valid public key.
        /// </summary>
        private static byte[] BuildTls13ServerHello(byte[] sessionId, int cipherSuite)
        {
            var body = new MemoryStream();
            WriteServerHelloPrefix(body, sessionId, cipherSuite);

            var extensions = new List<byte>
            {
                0x00, 0x2B, 0x00, 0x02, 0x03, 0x04, // supported_versions: TLS 1.3
                0x00, 0x33, 0x00, 0x24, // key_share, length 36
                (byte)(X25519 >> 8), (byte)X25519, 0x00, 0x20,
            };
            extensions.Add(9);
            for (int i = 1; i < 32; i++)
                extensions.Add(0);

            WriteExtensions(body, extensions.ToArray());
            return body.ToArray();
        }

        private static void WriteServerHelloPrefix(MemoryStream body, byte[] sessionId, int cipherSuite)
        {
            body.WriteByte(0x03);
            body.WriteByte(0x03);
            for (int i = 0; i < 32; i++)
                body.WriteByte(0x5A); // random: not a downgrade sentinel
            body.WriteByte((byte)sessionId.Length);
            body.Write(sessionId, 0, sessionId.Length);
            body.WriteByte((byte)(cipherSuite >> 8));
            body.WriteByte((byte)cipherSuite);
            body.WriteByte(0x00); // compression null
        }

        private static void WriteExtensions(MemoryStream body, byte[] extensions)
        {
            body.WriteByte((byte)(extensions.Length >> 8));
            body.WriteByte((byte)extensions.Length);
            body.Write(extensions, 0, extensions.Length);
        }

        private static void WriteHandshakeRecord(Stream stream, byte type, byte[] body)
        {
            int length = 4 + body.Length;
            var record = new byte[5 + length];
            record[0] = ClientHelloCapture.HandshakeRecord;
            record[1] = 0x03;
            record[2] = 0x03;
            record[3] = (byte)(length >> 8);
            record[4] = (byte)length;
            record[5] = type;
            record[6] = (byte)(body.Length >> 16);
            record[7] = (byte)(body.Length >> 8);
            record[8] = (byte)body.Length;
            Array.Copy(body, 0, record, 9, body.Length);
            stream.Write(record, 0, record.Length);
        }
    }
}
