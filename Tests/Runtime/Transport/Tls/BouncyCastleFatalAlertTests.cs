using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Retry;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #164: a fatal alert received from the server during the BouncyCastle handshake is
    /// classified by its AlertDescription (RFC 8446 section 6.2, RFC 5246 section 7.2.2):
    /// certificate/negotiation/protocol verdicts are non-retryable TLS errors; internal_error
    /// stays a retryable network error.
    /// </summary>
    [TestFixture]
    public class BouncyCastleFatalAlertTests
    {
        private const int HandshakeFailure = 40;
        private const int BadCertificate = 42;
        private const int ProtocolVersion = 70;
        private const int InternalError = 80;

        /// <summary>Reads the ClientHello record, answers with one fatal alert record.</summary>
        private static BouncyCastleLoopbackServer StartAlertServer(byte alertDescription)
        {
            return BouncyCastleLoopbackServer.StartRaw(stream =>
            {
                var header = new byte[5];
                BouncyCastleLoopbackServer.ReadExact(stream, header, 5);
                int length = (header[3] << 8) | header[4];
                BouncyCastleLoopbackServer.ReadExact(stream, new byte[length], length);

                // TLSPlaintext { alert(21), legacy_record_version 0x0303, length 2, fatal(2), desc }
                var alert = new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, alertDescription };
                stream.Write(alert, 0, alert.Length);
                stream.Flush();
                BouncyCastleLoopbackServer.DrainUntilClosed(stream);
            });
        }

        private static async Task<TlsResult> HandshakeAsync(ITlsProvider provider, int port)
        {
            // Only failing handshakes use this helper; the provider disposes the stream on failure.
            using var stream = await BouncyCastleLoopbackServer.ConnectAsync(port).ConfigureAwait(false);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await provider.WrapAsync(stream, "localhost", new[] { "http/1.1" }, cts.Token)
                .ConfigureAwait(false);
        }

        [TestCase(HandshakeFailure, "handshake_failure")]
        [TestCase(ProtocolVersion, "protocol_version")]
        [TestCase(BadCertificate, "bad_certificate")]
        public void ReceivedNonTransientAlert_ThrowsAuthenticationException(int description, string name)
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrIgnore();
            using var server = StartAlertServer((byte)description);

            var ex = AssertAsync.ThrowsAsync<AuthenticationException>(
                async () => await HandshakeAsync(provider, server.Port));

            StringAssert.Contains(name + "(" + description + ")", ex.Message);
            StringAssert.Contains("received from the server", ex.Message);
        }

        [Test]
        public void ReceivedInternalError_StaysNetworkError()
        {
            var provider = BouncyCastleLoopbackServer.GetProviderOrIgnore();
            using var server = StartAlertServer(InternalError);

            Exception failure = null;
            try
            {
                AssertAsync.Run(async () => await HandshakeAsync(provider, server.Port));
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Assert.IsInstanceOf<IOException>(failure);
            StringAssert.Contains("internal_error(80)", failure?.Message);
        }

        [TestCase(HandshakeFailure, UHttpErrorType.CertificateError, 1)]
        [TestCase(ProtocolVersion, UHttpErrorType.CertificateError, 1)]
        [TestCase(BadCertificate, UHttpErrorType.CertificateError, 1)]
        [TestCase(InternalError, UHttpErrorType.NetworkError, 3)]
        public void RetryInterceptor_RetriesOnlyTransientAlerts(
            int description, UHttpErrorType expectedType, int expectedConnections)
        {
            BouncyCastleLoopbackServer.GetProviderOrIgnore();
            using var server = StartAlertServer((byte)description);

            var ex = AssertAsync.ThrowsAsync<UHttpException>(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle);
                var retry = new RetryInterceptor(new RetryPolicy
                {
                    MaxRetries = 2,
                    InitialDelay = TimeSpan.FromMilliseconds(1),
                    UseJitter = false
                });
                var pipeline = new TestInterceptorPipeline(new IHttpInterceptor[] { retry }, transport);

                var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://localhost:{server.Port}/alert"))
                    .WithTimeout(TimeSpan.FromSeconds(30));
                using var response = await pipeline.ExecuteAsync(request, new RequestContext(request));
            });

            Assert.AreEqual(expectedType, ex.HttpError.Type, ex.Message);
            Assert.AreEqual(expectedConnections, server.AcceptedConnections,
                "Connections opened: 1 = not retried, 3 = MaxRetries 2 honoured.");
        }
    }
}
