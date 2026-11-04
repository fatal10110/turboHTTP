using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #175: every TLS handshake of the pool offers the one shared ALPN list instead of a new
    /// array per connection.
    /// </summary>
    [TestFixture]
    public class TcpConnectionPoolAlpnArrayTests
    {
        private sealed class RecordingTlsProvider : ITlsProvider
        {
            public readonly ConcurrentQueue<string[]> Offers = new ConcurrentQueue<string[]>();

            public string ProviderName => "Recording";

            public bool IsAlpnSupported() => true;

            public Task<TlsResult> WrapAsync(
                Stream innerStream, string host, string[] alpnProtocols, CancellationToken ct)
            {
                Offers.Enqueue(alpnProtocols);
                return Task.FromException<TlsResult>(new AuthenticationException("recording provider"));
            }
        }

        [Test]
        public void SecureConnects_OfferTheSharedAlpnArray()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var provider = new RecordingTlsProvider();

                Task.Run(async () =>
                {
                    using var pool = new TcpConnectionPool(_ => provider, TlsBackend.SslStream);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    for (int i = 0; i < 2; i++)
                    {
                        // The connect completes from the listen backlog; the provider then fails.
                        try
                        {
                            using var lease = await pool.GetConnectionAsync("127.0.0.1", port, true, cts.Token);
                            Assert.Fail("The recording provider must fail the handshake.");
                        }
                        catch (Exception ex) when (!(ex is AssertionException))
                        {
                        }
                    }
                }).GetAwaiter().GetResult();

                Assert.AreEqual(2, provider.Offers.Count);
                foreach (var offer in provider.Offers)
                {
                    Assert.AreSame(AlpnProtocols.H2AndHttp11, offer,
                        "A new ALPN array was allocated for this handshake.");
                }

                CollectionAssert.AreEqual(new[] { "h2", "http/1.1" }, AlpnProtocols.H2AndHttp11);
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
