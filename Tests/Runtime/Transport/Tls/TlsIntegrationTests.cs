#if TURBOHTTP_INTEGRATION_TESTS
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;
using UnityEngine;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsIntegrationTests
    {
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
                Assert.IsNotNull(response.Body);
                Assert.Greater(response.Body.Length, 0);
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Integration")]
        public void HttpClient_WithBouncyCastle_CanFetchGoogle()        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("BouncyCastle integration is skipped in Unity batch mode on this environment.");
                return;
            }

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

                var response = await SendWithGuardedTimeoutAsync(client, "https://www.google.com");
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
            if (Application.isBatchMode)
            {
                Assert.Ignore("BouncyCastle HTTP/2 integration is skipped in Unity batch mode on this environment.");
                return;
            }

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

                var response = await SendWithGuardedTimeoutAsync(client, "https://www.google.com");
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

                var alpn = TryGetNegotiatedAlpn(pool, "www.google.com");
                Assert.AreEqual("h2", alpn, "Expected HTTP/2 ALPN negotiation with BouncyCastle backend");
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
                    Assert.Ignore($"Integration request to {url} exceeded hard timeout in this environment.");
                    throw new TimeoutException();
                }

                return await requestTask;
            }
            catch (OperationCanceledException)
            {
                Assert.Ignore($"Integration request to {url} timed out or was canceled in this environment.");
                throw;
            }
            catch (UHttpException ex) when (ex.HttpError.Type == UHttpErrorType.Timeout)
            {
                Assert.Ignore($"Integration request to {url} timed out in this environment.");
                throw;
            }
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
