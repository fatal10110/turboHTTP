using System;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142 (SNI part of #46): the SslStream provider's ClientHello, captured by a scripted
    /// loopback server. Callers pass <see cref="Uri.Host"/>; the provider sends the A-label of an
    /// IDN name and no server_name for an IPv4/IPv6 literal (RFC 6066 section 3), except on Unity's
    /// Mono/UnityTls SslStream, which sends the address (a documented limitation, #46).
    /// </summary>
    [TestFixture]
    public class SslStreamServerNameTests
    {
        [TestCase("b\u00fccher.example", "xn--bcher-kva.example")]
        [TestCase("localhost", "localhost")]
        public void SslStream_ClientHello_SendsAsciiServerName(string host, string expected)
        {
            var hello = CaptureSslStreamClientHello(host, ipLiteral: false);

            CollectionAssert.AreEqual(new[] { expected }, ClientHelloCapture.ServerNames(hello),
                ClientHelloCapture.Describe(hello));
        }

        [TestCase("127.0.0.1")]
        [TestCase("[::1]")]
        public void SslStream_ClientHello_IpLiteral_SendsNoServerName(string host)
        {
            var hello = CaptureSslStreamClientHello(host, ipLiteral: true);

            if (ClientHelloCapture.ServerNames(hello) != null && IsMonoSslStream)
            {
                // Documented limitation (Documentation~/PlatformNotes.md, #46): Unity's Mono
                // SslStream (UnityTls) derives server_name from the target host, also for an
                // address, and the target host is also the name the certificate is checked
                // against, so the provider cannot blank it. TlsBackend.BouncyCastle sends none.
                TestRequirements.IgnoreEnvironment(
                    "Unity Mono/UnityTls SslStream sends an IP literal as server_name (known limitation, #46). " +
                    ClientHelloCapture.Describe(hello));
            }

            Assert.IsNull(ClientHelloCapture.ServerNames(hello),
                "RFC 6066 section 3: literal addresses are not permitted in server_name. " +
                ClientHelloCapture.Describe(hello));
        }

        private static bool IsMonoSslStream => Type.GetType("Mono.Runtime") != null;

        private static byte[] CaptureSslStreamClientHello(string host, bool ipLiteral)
        {
            byte[] clientHello = null;
            Exception clientError = null;
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                Volatile.Write(ref clientHello, ClientHelloCapture.ReadHandshakeMessage(stream));
                ClientHelloCapture.WriteHandshakeFailureAlert(stream);
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            AssertAsync.Run(async () =>
            {
                var stream = await BouncyCastleLoopbackServer.ConnectAsync(server.Port);
                // Longer than the server's bound (one more receive timeout included), so a hung
                // client is reported by the server as "no ClientHello", not by a cancelled client.
                using var cts = new CancellationTokenSource(
                    ClientHelloCapture.HelloBound + ClientHelloCapture.HelloBound + TimeSpan.FromSeconds(15));
                try
                {
                    var result = await SslStreamTlsProvider.Instance.WrapAsync(
                        stream, host, new[] { "http/1.1" }, cts.Token);
                    result.SecureStream.Dispose();
                    Assert.Fail("The scripted server refuses every handshake.");
                }
                catch (Exception ex) when (!(ex is AssertionException))
                {
                    // Expected: the server answered with a fatal alert.
                    Volatile.Write(ref clientError, ex);
                }
                finally
                {
                    stream.Dispose();
                }
            });

            var captured = Volatile.Read(ref clientHello);
            if (captured == null)
            {
                var detail = "server: " + server.HandlerError + "; client: " + Volatile.Read(ref clientError);
                if (ipLiteral && IsMonoSslStream && server.HandlerError is TimeoutException)
                {
                    // API 29 emulator (#330): Unity's Mono/UnityTls SslStream put no ClientHello on
                    // the wire for an IPv6 literal target host within the bound, so there is no
                    // server_name to check. The loopback connection itself is IPv4 (127.0.0.1).
                    TestRequirements.IgnoreEnvironment(
                        "Unity Mono/UnityTls SslStream sent no ClientHello for the IP literal target host '" + host +
                        "' within " + ClientHelloCapture.HelloBound.TotalSeconds + " s. " + detail);
                }

                Assert.Fail("The server did not capture a ClientHello: " + detail);
            }

            return captured;
        }
    }
}
