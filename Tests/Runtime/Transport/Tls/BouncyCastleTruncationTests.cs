using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142: an HTTP/1.1 response whose body is delimited by connection close (RFC 9112 section
    /// 6.3, rule 8) and whose TLS connection ends without close_notify is truncated (RFC 8446
    /// section 6.1): the request fails instead of returning the partial body as complete.
    /// </summary>
    [TestFixture]
    public class BouncyCastleTruncationTests
    {
        [Test]
        public void CloseDelimitedBody_CutWithoutCloseNotify_FailsAsTruncated()
        {
            BouncyCastleLoopbackServer.GetProviderOrFail();
            var certificate = LocalTlsTestServer.TryCreateLocalhostCertificate();
            if (certificate == null)
            {
                TestRequirements.IgnoreEnvironment("The committed test certificate (TestLocalhostCertificate) could not be loaded in this runtime.");
                return;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Exception serverError = null;
            using var serverDone = new ManualResetEventSlim(false);
            var serverThread = new Thread(() =>
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                    client.ReceiveTimeout = 30000;
                    client.SendTimeout = 30000;
                    var network = client.GetStream();
                    var ssl = new SslStream(network, leaveInnerStreamOpen: true);
                    ssl.AuthenticateAsServer(certificate, false, SslProtocols.Tls12, false);
                    ReadRequestHead(ssl);

                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\npartial-body");
                    ssl.Write(response, 0, response.Length);
                    ssl.Flush();

                    // Close the TCP connection under the TLS session: no close_notify is sent.
                    client.Client.Shutdown(SocketShutdown.Both);
                    client.Close();
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref serverError, ex);
                }
                finally
                {
                    serverDone.Set();
                }
            })
            { IsBackground = true, Name = "bc-truncation-server" };
            serverThread.Start();

            try
            {
                Exception failure = null;
                UHttpResponse unexpected = null;
                AssertAsync.Run(async () =>
                {
                    using var pool = new TcpConnectionPool(maxConnectionsPerHost: 1, tlsBackend: TlsBackend.BouncyCastle);
                    using var transport = new RawSocketTransport(pool, TlsBackend.BouncyCastle);
                    var request = new UHttpRequest(HttpMethod.GET, new Uri($"https://localhost:{port}/truncated"))
                        .WithTimeout(TimeSpan.FromSeconds(30));
                    try
                    {
                        unexpected = await TransportDispatchHelper.CollectResponseAsync(
                            transport.DispatchAsync, request, new RequestContext(request));
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                });

                Assert.IsTrue(serverDone.Wait(TimeSpan.FromSeconds(30)), "The TLS server did not finish.");
                Assert.IsNull(Volatile.Read(ref serverError), "TLS server failed: " + Volatile.Read(ref serverError));
                Assert.IsNull(unexpected,
                    "A truncated close-delimited body was returned as complete: " +
                    (unexpected == null ? "" : unexpected.GetBodyAsString()));
                Assert.IsInstanceOf<UHttpException>(failure, failure?.ToString());
                var error = ((UHttpException)failure).HttpError;
                Assert.AreEqual(UHttpErrorType.NetworkError, error.Type, failure.ToString());
                Assert.IsTrue(ChainContainsType(failure, "TlsNoCloseNotifyException"),
                    "The failure must come from the missing close_notify: " + failure);
            }
            finally
            {
                listener.Stop();
                certificate.Dispose();
            }
        }

        private static bool ChainContainsType(Exception ex, string typeName)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e.GetType().Name == typeName)
                    return true;
            }

            return false;
        }

        private static void ReadRequestHead(Stream stream)
        {
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (head.Length > 16 * 1024)
                    throw new InvalidDataException("Request head too large.");
                BouncyCastleLoopbackServer.ReadExact(stream, one, 1);
                head.Append((char)one[0]);
            }
        }
    }
}
