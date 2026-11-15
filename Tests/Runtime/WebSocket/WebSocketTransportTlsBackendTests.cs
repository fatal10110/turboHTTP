using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #1091: the TLS backend passed to <see cref="RawSocketWebSocketTransport"/> is used unless the
    /// connection options set a backend other than Auto; the cloned options used to make the constructor
    /// value unreachable.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketTransportTlsBackendTests
    {
        private ITlsProvider _previousBouncyCastle;

        [SetUp]
        public void SetUp()
        {
            _previousBouncyCastle = TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(null);
        }

        [TearDown]
        public void TearDown()
        {
            TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(_previousBouncyCastle);
        }

        [TestCase(TlsBackend.Auto, TlsBackend.BouncyCastle, TlsBackend.BouncyCastle)]
        [TestCase(TlsBackend.Auto, TlsBackend.SslStream, TlsBackend.SslStream)]
        [TestCase(TlsBackend.SslStream, TlsBackend.BouncyCastle, TlsBackend.SslStream)]
        [TestCase(TlsBackend.BouncyCastle, TlsBackend.SslStream, TlsBackend.BouncyCastle)]
        [TestCase(TlsBackend.Auto, TlsBackend.Auto, TlsBackend.Auto)]
        public void ResolveTlsBackend_NonAutoOptionWins_ElseTransportBackend(
            TlsBackend optionBackend,
            TlsBackend transportBackend,
            TlsBackend expected)
        {
            var options = new WebSocketConnectionOptions { TlsBackend = optionBackend };

            Assert.AreEqual(expected, RawSocketWebSocketTransport.ResolveTlsBackend(options, transportBackend));
        }

        [Test]
        public void Wss_ConstructorBackend_UsedWhenOptionsLeaveAuto()
        {
            AssertAsync.Run(async () =>
            {
                var provider = new RecordingTlsProvider();
                TlsProviderSelector.ExchangeBouncyCastleProviderForTesting(provider);

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var accept = listener.AcceptTcpClientAsync();
                try
                {
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    using var transport = new RawSocketWebSocketTransport(TlsBackend.BouncyCastle);
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                    // Options keep the default TlsBackend.Auto: the constructor's BouncyCastle applies. The old
                    // code resolved Auto (SslStream), which waited on the silent listener until the guard.
                    var ex = AssertAsync.ThrowsAsync<IOException>(() => transport.ConnectAsync(
                        new Uri("wss://127.0.0.1:" + port + "/"),
                        new WebSocketConnectionOptions(),
                        cts.Token));

                    Assert.AreSame(RecordingTlsProvider.Failure, ex);
                    Assert.AreEqual(1, provider.WrapCalls, "The constructor's TLS backend was not used.");
                }
                finally
                {
                    listener.Stop();
                    try { (await accept.ConfigureAwait(false)).Close(); } catch { }
                }
            });
        }

        /// <summary>Stands in for the BouncyCastle provider: records the call and fails the handshake.</summary>
        private sealed class RecordingTlsProvider : ITlsProvider
        {
            internal static readonly IOException Failure = new IOException("recording provider: handshake stopped");

            private int _wrapCalls;

            public int WrapCalls => Volatile.Read(ref _wrapCalls);

            public string ProviderName => "BouncyCastle";

            public bool IsAlpnSupported() => true;

            public Task<TlsResult> WrapAsync(
                Stream innerStream, string host, string[] alpnProtocols, CancellationToken ct)
            {
                Interlocked.Increment(ref _wrapCalls);
                return Task.FromException<TlsResult>(Failure);
            }
        }
    }
}
