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
    /// #141: guards for the re-vendored (stripped) BouncyCastle library, driven through the real
    /// provider against a scripted loopback server.
    /// <list type="bullet">
    /// <item>A server certificate nested 10,000 SEQUENCEs deep fails the handshake on the ASN.1
    /// nesting bound (default depth 64) with a non-retryable <see cref="AuthenticationException"/>;
    /// the old library recursed without a bound.</item>
    /// <item>The ClientHello offers exactly the AEAD/ECDHE cipher suites of the strip (decision B:
    /// no CBC; no DHE, static RSA, PSK or SRP key exchange).</item>
    /// </list>
    /// </summary>
    [TestFixture]
    public class BouncyCastleHostileCertificateTests
    {
        private const byte HandshakeRecord = 22;
        private const byte AlertRecord = 21;
        private const byte ClientHelloType = 1;
        private const byte ServerHelloType = 2;
        private const byte CertificateType = 11;
        private const int MaxRecordFragment = 16384;
        private const int NestingLevels = 10000;

        // Wire values from the IANA TLS Cipher Suites registry (RFC 8446 B.4, RFC 5289, RFC 7905).
        private static readonly int[] ExpectedCipherSuites =
        {
            0x1303, // TLS_CHACHA20_POLY1305_SHA256
            0x1301, // TLS_AES_128_GCM_SHA256
            0x1302, // TLS_AES_256_GCM_SHA384
            0xCCA9, // TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256
            0xC02B, // TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256
            0xC02C, // TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384
            0xCCA8, // TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
            0xC02F, // TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256
            0xC030, // TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
            0x00FF, // TLS_EMPTY_RENEGOTIATION_INFO_SCSV (RFC 5746 section 3.4)
        };

        [Test]
        public void DeeplyNestedServerCertificate_FailsWithBadCertificate()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                ReadHandshakeMessage(stream);
                WriteHandshake(stream, ServerHelloType, BuildTls12ServerHello());
                WriteHandshake(stream, CertificateType, BuildCertificateMessage(BuildNestedSequences(NestingLevels)));
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            var ex = AssertAsync.ThrowsAsync<AuthenticationException>(
                async () => await HandshakeAsync(provider, server.Port));

            StringAssert.Contains("bad_certificate(42)", ex.Message);
            StringAssert.Contains("sent to the server", ex.Message);
            Assert.IsTrue(ChainContains(ex, "maximum nested construction level"),
                "The certificate must be rejected by the ASN.1 nesting bound, not by a later check: " + ex);
            Assert.IsNull(server.HandlerError, "Scripted server failed: " + server.HandlerError);
        }

        [Test]
        public void ClientHello_OffersExactlyTheStrippedCipherSuites()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrFail();
            byte[] clientHello = null;
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                Volatile.Write(ref clientHello, ReadHandshakeMessage(stream));

                // TLSPlaintext { alert(21), 0x0303, length 2, fatal(2), handshake_failure(40) }
                var alert = new byte[] { AlertRecord, 0x03, 0x03, 0x00, 0x02, 0x02, 40 };
                stream.Write(alert, 0, alert.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            AssertAsync.ThrowsAsync<AuthenticationException>(
                async () => await HandshakeAsync(provider, server.Port));

            var captured = Volatile.Read(ref clientHello);
            Assert.IsNotNull(captured, "The server did not capture a ClientHello: " + server.HandlerError);
            CollectionAssert.AreEqual(ExpectedCipherSuites, ParseCipherSuites(captured),
                "Offered: " + string.Join(", ", ParseCipherSuites(captured).ConvertAll(s => "0x" + s.ToString("X4"))));
        }

        private static async Task<TlsResult> HandshakeAsync(ITlsProvider provider, int port)
        {
            // Only failing handshakes use this helper; the provider disposes the stream on failure.
            using var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await provider.WrapAsync(stream, "localhost", new[] { "http/1.1" }, cts.Token)
                .ConfigureAwait(false);
        }

        private static bool ChainContains(Exception ex, string text)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.Message != null && e.Message.IndexOf(text, StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads handshake records until one whole handshake message is buffered; returns it with
        /// its 4-byte header (msg_type, uint24 length).
        /// </summary>
        private static byte[] ReadHandshakeMessage(Stream stream)
        {
            var message = new MemoryStream();
            var header = new byte[5];
            while (true)
            {
                BouncyCastleLoopbackServer.ReadExact(stream, header, 5);
                int length = (header[3] << 8) | header[4];
                if (header[0] != HandshakeRecord || length > MaxRecordFragment + 256)
                    throw new InvalidDataException("Expected a handshake record, got type " + header[0]);

                var fragment = new byte[length];
                BouncyCastleLoopbackServer.ReadExact(stream, fragment, length);
                message.Write(fragment, 0, length);

                var buffered = message.ToArray();
                if (buffered.Length >= 4 &&
                    buffered.Length >= 4 + ((buffered[1] << 16) | (buffered[2] << 8) | buffered[3]))
                    return buffered;
            }
        }

        /// <summary>Extracts the cipher_suites vector of a ClientHello (RFC 8446 section 4.1.2).</summary>
        private static List<int> ParseCipherSuites(byte[] message)
        {
            Assert.AreEqual(ClientHelloType, message[0], "Not a ClientHello.");
            int offset = 4 + 2 + 32; // header, legacy_version, random
            offset += 1 + message[offset]; // legacy_session_id
            int length = (message[offset] << 8) | message[offset + 1];
            offset += 2;

            var suites = new List<int>(length / 2);
            for (int i = 0; i < length; i += 2)
                suites.Add((message[offset + i] << 8) | message[offset + i + 1]);
            return suites;
        }

        /// <summary>
        /// TLS 1.2 ServerHello (no supported_versions, RFC 8446 section 4.1.3) selecting
        /// TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, with an empty renegotiation_info (RFC 5746).
        /// </summary>
        private static byte[] BuildTls12ServerHello()
        {
            var body = new MemoryStream();
            body.WriteByte(0x03);
            body.WriteByte(0x03);
            for (int i = 0; i < 32; i++)
                body.WriteByte(0x5A); // random: not the TLS 1.3 downgrade sentinel
            body.WriteByte(0x00); // empty legacy_session_id: nothing to resume
            body.WriteByte(0xC0);
            body.WriteByte(0x2F);
            body.WriteByte(0x00); // compression null
            var extensions = new byte[] { 0xFF, 0x01, 0x00, 0x01, 0x00 };
            body.WriteByte(0x00);
            body.WriteByte((byte)extensions.Length);
            body.Write(extensions, 0, extensions.Length);
            return body.ToArray();
        }

        /// <summary>
        /// <paramref name="levels"/> nested BER SEQUENCE headers with indefinite length
        /// (0x30 0x80 each). A definite-length encoding of this depth needs about 4 bytes per level
        /// and would exceed BouncyCastle's 32 KiB handshake-message cap; a bounded parser stops at
        /// its depth limit long before the missing end-of-contents octets matter.
        /// </summary>
        private static byte[] BuildNestedSequences(int levels)
        {
            var der = new byte[levels * 2];
            for (int i = 0; i < der.Length; i += 2)
            {
                der[i] = 0x30;
                der[i + 1] = 0x80;
            }

            return der;
        }

        /// <summary>TLS 1.2 Certificate body: one certificate_list entry (RFC 5246 section 7.4.2).</summary>
        private static byte[] BuildCertificateMessage(byte[] certificate)
        {
            var body = new MemoryStream();
            WriteUInt24(body, certificate.Length + 3);
            WriteUInt24(body, certificate.Length);
            body.Write(certificate, 0, certificate.Length);
            return body.ToArray();
        }

        /// <summary>Writes one handshake message, fragmented into records of at most 2^14 bytes.</summary>
        private static void WriteHandshake(Stream stream, byte type, byte[] body)
        {
            var message = new MemoryStream();
            message.WriteByte(type);
            WriteUInt24(message, body.Length);
            message.Write(body, 0, body.Length);
            var bytes = message.ToArray();

            for (int offset = 0; offset < bytes.Length; offset += MaxRecordFragment)
            {
                int length = Math.Min(MaxRecordFragment, bytes.Length - offset);
                var header = new byte[] { HandshakeRecord, 0x03, 0x03, (byte)(length >> 8), (byte)length };
                stream.Write(header, 0, header.Length);
                stream.Write(bytes, offset, length);
            }
        }

        private static void WriteUInt24(Stream stream, int value)
        {
            stream.WriteByte((byte)(value >> 16));
            stream.WriteByte((byte)(value >> 8));
            stream.WriteByte((byte)value);
        }
    }
}
