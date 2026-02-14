#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Collections.Concurrent;
using System.Net;
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
        [Test]
        [Category("Integration")]
        public void HttpClient_WithSslStream_CanFetchGoogle()        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    TlsBackend = TlsBackend.SslStream
                });

                var response = await client.Get("https://www.google.com").SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsNotNull(response.Body);
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

                UHttpResponse response;
                try
                {
                    response = await client.Get("https://www.google.com").SendAsync();
                }
                catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Timeout)
                {
                    Assert.Ignore("BouncyCastle integration request timed out in this environment.");
                    return;
                }
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsNotNull(response.Body);
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

                var response = await client.Get("https://httpbin.org/get").SendAsync();
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
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            Task.Run(async () =>
            {
                using var pool = new TcpConnectionPool(tlsBackend: TlsBackend.BouncyCastle);
                using var transport = new RawSocketTransport(pool);
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true
                });

                UHttpResponse response;
                try
                {
                    response = await client.Get("https://www.google.com").SendAsync();
                }
                catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Timeout)
                {
                    Assert.Ignore("BouncyCastle HTTP/2 integration request timed out in this environment.");
                    return;
                }
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var alpn = TryGetNegotiatedAlpn(pool, "www.google.com");
                Assert.AreEqual("h2", alpn, "Expected HTTP/2 ALPN negotiation with BouncyCastle backend");
            }).GetAwaiter().GetResult();
        }

        private static string TryGetProviderName(TcpConnectionPool pool, string hostFragment)
        {
            var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);

            foreach (var kv in idle)
            {
                if (!kv.Key.Contains(hostFragment, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kv.Value.TryPeek(out var conn))
                    return conn.TlsProviderName;
            }

            return null;
        }

        private static string TryGetNegotiatedAlpn(TcpConnectionPool pool, string hostFragment)
        {
            var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(pool);

            foreach (var kv in idle)
            {
                if (!kv.Key.Contains(hostFragment, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (kv.Value.TryPeek(out var conn))
                    return conn.NegotiatedAlpnProtocol;
            }

            return null;
        }
    }
}
#endif
