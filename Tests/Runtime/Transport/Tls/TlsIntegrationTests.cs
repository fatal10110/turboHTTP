#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsIntegrationTests
    {
        // Valid-chain hosts must chain to a root every target store has: Android 6 (API 23) lacks
        // ISRG Root X1/X2 and Amazon Root CA 1, so Let's Encrypt (sha256.badssl.com) and Amazon
        // (httpbin.org) hosts fail there by design (#361). www.google.com chains to GlobalSign Root CA
        // (TlsPublicHostTests documents the chain) and serves h2.
        private const string ValidChainHost = "www.google.com";
        private const string BouncyFetchUrl = TlsPublicHostTests.ValidChainUrl;
        private const string Http2ProbeUrl = TlsPublicHostTests.ValidChainUrl;

        [SetUp]
        public void SetUp()
        {
            HttpTransportFactory.Reset();
            RawSocketTransport.EnsureRegistered();
        }

        [TearDown]
        public void TearDown()
        {
            HttpTransportFactory.Reset();
        }

        [Test]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void HttpClient_WithSslStream_CanFetchGoogle()        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    TlsBackend = TlsBackend.SslStream
                });

                var response = await SendWithGuardedTimeoutAsync(client, "https://www.google.com");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.Greater(response.Body.Length, 0);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void HttpClient_WithBouncyCastle_CanFetchGoogle()        {
            TestRequirements.RequireBouncyCastle();

            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    TlsBackend = TlsBackend.BouncyCastle
                });

                var response = await SendWithGuardedTimeoutAsync(client, BouncyFetchUrl);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.Greater(response.Body.Length, 0);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void HttpClient_Auto_SelectsCorrectProvider()        {
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.Auto);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await SendWithGuardedTimeoutAsync(client, TlsPublicHostTests.ValidChainUrl);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var providerName = TryGetProviderName(pool, ValidChainHost);
                Assert.IsNotNull(providerName, "Expected to capture TLS provider name from pooled connection");

#if UNITY_EDITOR || UNITY_STANDALONE_WIN || UNITY_STANDALONE_OSX
                Assert.AreEqual("SslStream", providerName);
#endif
#if UNITY_STANDALONE_LINUX
                Assert.That(providerName, Is.EqualTo("BouncyCastle").Or.EqualTo("SslStream"));
#endif
#if UNITY_IOS || UNITY_ANDROID
                var sslProvider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
                if (!sslProvider.IsAlpnSupported() && TlsProviderSelector.IsBouncyCastleAvailable())
                    Assert.AreEqual("BouncyCastle", providerName);
                else
                    Assert.AreEqual("SslStream", providerName);
#endif
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        [Category("ExternalNetwork")]
        public void HttpClient_Http2_Works()        {
            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.Auto);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                var response = await SendWithGuardedTimeoutAsync(client, Http2ProbeUrl);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                bool usedHttp2 = TryHasHttp2Connection(transport, ValidChainHost, 443);
                string negotiatedAlpn = TryGetNegotiatedAlpn(pool, ValidChainHost);
                // #413: h2 where the pool's provider reports ALPN, an honest skip where it has none.
                AlpnAssert.UsedHttp2(PoolProvider(pool, ValidChainHost), negotiatedAlpn, usedHttp2, ValidChainHost);
            }).GetAwaiter().GetResult();
        }

        private static async Task<UHttpResponse> SendWithGuardedTimeoutAsync(UHttpClient client, string url)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                var requestTask = Task.Run(async () => await client.Get(url).SendBufferedAsync(cts.Token));
                var completed = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(35), CancellationToken.None));
                if (completed != requestTask)
                {
                    TestRequirements.IgnoreEnvironment($"Integration request to {url} exceeded the hard timeout in this environment.");
                }

                return await requestTask;
            }
            catch (Exception ex) when (
                ex is OperationCanceledException ||
                (ex is UHttpException timeout && timeout.HttpError.Type == UHttpErrorType.Timeout))
            {
                // One clause: under IL2CPP an exception thrown by an earlier clause of a filtered try is
                // caught by a later one (#320). An unreachable host is an environment skip, never a
                // pass; the reason keeps the transport's message (#361).
                TestRequirements.IgnoreEnvironment(
                    $"Integration request to {url} timed out or was canceled in this environment: {ex.Message}");
                throw;
            }
        }

        private static string TryGetProviderName(TcpConnectionPool pool, string hostFragment)
        {
            return pool.TryGetTlsProviderName(hostFragment, out var providerName)
                ? providerName
                : null;
        }

        private static ITlsProvider PoolProvider(TcpConnectionPool pool, string hostFragment)
        {
            string providerName = TryGetProviderName(pool, hostFragment);
            Assert.IsNotNull(providerName, "Expected to capture TLS provider name from pooled connection");
            return TlsProviderSelector.GetProvider(
                providerName == "BouncyCastle" ? TlsBackend.BouncyCastle : TlsBackend.SslStream);
        }

        private static string TryGetNegotiatedAlpn(TcpConnectionPool pool, string hostFragment)
        {
            return pool.TryGetNegotiatedAlpnProtocol(hostFragment, out var negotiatedAlpn)
                ? negotiatedAlpn
                : null;
        }

        private static bool TryHasHttp2Connection(RawSocketTransport transport, string host, int port)
        {
            return transport.HasHttp2Connection(host, port);
        }
    }
}
#endif
