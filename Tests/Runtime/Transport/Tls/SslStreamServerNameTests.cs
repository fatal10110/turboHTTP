using System;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142 (SNI part of #46): the SslStream provider's ClientHello, captured by a scripted
    /// loopback server. Callers pass <see cref="Uri.Host"/>; the provider sends the A-label of an
    /// IDN name and no server_name for an IPv4/IPv6 literal (RFC 6066 section 3).
    /// </summary>
    [TestFixture]
    public class SslStreamServerNameTests
    {
        [TestCase("b\u00fccher.example", "xn--bcher-kva.example")]
        [TestCase("localhost", "localhost")]
        public void SslStream_ClientHello_SendsAsciiServerName(string host, string expected)
        {
            var hello = CaptureSslStreamClientHello(host);

            CollectionAssert.AreEqual(new[] { expected }, ClientHelloCapture.ServerNames(hello),
                ClientHelloCapture.Describe(hello));
        }

        [TestCase("127.0.0.1")]
        [TestCase("[::1]")]
        public void SslStream_ClientHello_IpLiteral_SendsNoServerName(string host)
        {
            var hello = CaptureSslStreamClientHello(host);

            Assert.IsNull(ClientHelloCapture.ServerNames(hello),
                "RFC 6066 section 3: literal addresses are not permitted in server_name. " +
                ClientHelloCapture.Describe(hello));
        }

        private static byte[] CaptureSslStreamClientHello(string host)
        {
            byte[] clientHello = null;
            using var server = BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                Volatile.Write(ref clientHello, ClientHelloCapture.ReadHandshakeMessage(stream));
                ClientHelloCapture.WriteHandshakeFailureAlert(stream);
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });

            AssertAsync.Run(async () =>
            {
                var stream = await BouncyCastleLoopbackServer.ConnectAsync(server.Port);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
                }
                finally
                {
                    stream.Dispose();
                }
            });

            var captured = Volatile.Read(ref clientHello);
            Assert.IsNotNull(captured, "The server did not capture a ClientHello: " + server.HandlerError);
            return captured;
        }
    }
}
