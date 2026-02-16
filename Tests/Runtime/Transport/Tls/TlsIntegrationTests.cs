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
        private const string BouncyFetchUrl = "https://sha256.badssl.com/";
        private const string Http2ProbeUrl = "https://nghttp2.org/httpbin/get";

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
        public void HttpClient_WithBouncyCastle_CanFetchGoogle()        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

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

                var response = await SendWithGuardedTimeoutAsync(client, "https://httpbin.org/get");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var providerName = TryGetProviderName(pool, "httpbin.org");
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

                bool usedHttp2 = TryHasHttp2Connection(transport, "nghttp2.org", 443);
                string negotiatedAlpn = TryGetNegotiatedAlpn(pool, "nghttp2.org");
                if (string.Equals(negotiatedAlpn, "h2", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.IsTrue(
                        usedHttp2,
                        "Negotiated ALPN was h2 but HTTP/2 connection was not established.");
                }
                else
                {
                    Assert.IsFalse(
                        usedHttp2,
                        "Negotiated ALPN was not h2; expected HTTP/1.1 fallback.");
                    Assert.That(
                        negotiatedAlpn,
                        Is.Null.Or.EqualTo("http/1.1"),
                        "Expected ALPN fallback to be null or http/1.1.");
                }
            }).GetAwaiter().GetResult();
        }

        private static async Task<UHttpResponse> SendWithGuardedTimeoutAsync(UHttpClient client, string url)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            try
            {
                var requestTask = Task.Run(async () => await client.Get(url).SendAsync(cts.Token));
                var completed = await Task.WhenAny(requestTask, Task.Delay(TimeSpan.FromSeconds(35), CancellationToken.None));
                if (completed != requestTask)
                {
                    Assert.Fail($"Integration request to {url} exceeded hard timeout in this environment.");
                }

                return await requestTask;
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Integration request to {url} timed out or was canceled in this environment.");
                throw;
            }
            catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Timeout)
            {
                Assert.Fail($"Integration request to {url} timed out in this environment.");
                throw;
            }
        }

        private static string TryGetProviderName(TcpConnectionPool pool, string hostFragment)
        {
            return pool.TryGetTlsProviderName(hostFragment, out var providerName)
                ? providerName
                : null;
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
