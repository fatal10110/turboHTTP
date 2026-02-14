using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Auth;
using TurboHTTP.Core;
using TurboHTTP.JSON;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TurboHTTP.Tests.Runtime
{
    public class TestHttpClient : MonoBehaviour
    {
        private UHttpClient _client;
        private TcpConnectionPool _pool;
        private RawSocketTransport _transport;

        async void Start()
        {
            try
            {
                _pool = new TcpConnectionPool();
                _transport = new RawSocketTransport(_pool);
                _client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = _transport,
                    DisposeTransport = true
                });

                await TestBasicGet();
                await TestPostJson();
                await TestPostJsonString();
                await TestCustomHeaders();
                await TestTimeout();
                await TestConnectionReuse();
                await TestHeadRequest();
                await TestTlsVersion();
                await TestUnsupportedScheme();
                TestPlatformApiAvailability();
                await MeasureLatencyBaseline();

                _client.Dispose();
                Debug.Log("=== All tests complete ===");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Debug.LogError("=== TEST FAILURE ===");
            }
        }

        private async Task TestBasicGet()
        {
            var response = await _client.Get("https://httpbin.org/get").SendAsync();
            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Basic GET failed");
            var body = response.GetBodyAsString();
            Ensure(body != null && body.Contains("httpbin.org"), "Basic GET body missing expected content");
            Debug.Log("TestBasicGet passed");
        }

        private async Task TestPostJson()
        {
            var response = await _client.Post("https://httpbin.org/post")
                .WithJsonBody(new { key = "value" })
                .SendAsync();

            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Post JSON failed");
            var body = response.GetBodyAsString();
            Ensure(body != null && body.Contains("value"), "Post JSON body missing expected content");
            Debug.Log("TestPostJson passed");
        }

        private async Task TestPostJsonString()
        {
            var response = await _client.Post("https://httpbin.org/post")
                .WithJsonBody("{\"key\":\"value\"}")
                .SendAsync();

            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Post JSON string failed");
            var body = response.GetBodyAsString();
            Ensure(body != null && body.Contains("key"), "Post JSON string body missing expected content");
            Debug.Log("TestPostJsonString passed");
        }

        private async Task TestCustomHeaders()
        {
            var response = await _client.Get("https://httpbin.org/headers")
                .WithHeader("X-Custom", "abc")
                .WithBearerToken("token123")
                .SendAsync();

            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Custom headers failed");
            var body = response.GetBodyAsString();
            Ensure(body != null && body.Contains("X-Custom"), "Custom header not echoed");
            Ensure(body != null && body.Contains("Bearer token123"), "Bearer token not echoed");
            Debug.Log("TestCustomHeaders passed");
        }

        private async Task TestTimeout()
        {
            try
            {
                await _client.Get("https://httpbin.org/delay/10")
                    .WithTimeout(TimeSpan.FromSeconds(2))
                    .SendAsync();

                throw new Exception("Timeout test failed: request did not time out");
            }
            catch (UHttpException ex)
            {
                Ensure(ex.HttpError.Type == UHttpErrorType.Timeout, "Timeout test failed: wrong error type");
                Debug.Log("TestTimeout passed");
            }
        }

        private async Task TestConnectionReuse()
        {
            var sw = new Stopwatch();
            for (int i = 0; i < 3; i++)
            {
                sw.Restart();
                var response = await _client.Get("https://httpbin.org/get").SendAsync();
                sw.Stop();
                Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Connection reuse GET failed");
                Debug.Log($"TestConnectionReuse request {i + 1}: {sw.ElapsedMilliseconds}ms");
            }
        }

        private async Task TestHeadRequest()
        {
            var response = await _client.Head("https://httpbin.org/get").SendAsync();
            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "HEAD failed");
            Ensure(response.Body == null || response.Body.Length == 0, "HEAD should have empty body");
            Debug.Log("TestHeadRequest passed");
        }

        private async Task TestTlsVersion()
        {
            var response = await _client.Get("https://httpbin.org/get").SendAsync();
            Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "TLS test request failed");

            var idleField = typeof(TcpConnectionPool).GetField("_idleConnections", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var idle = (ConcurrentDictionary<string, ConcurrentQueue<PooledConnection>>)idleField.GetValue(_pool);

            foreach (var kvp in idle)
            {
                if (!kvp.Key.Contains("httpbin.org"))
                    continue;

                if (kvp.Value.TryPeek(out var conn))
                {
                    var tlsVersion = conn.TlsVersion;
                    var providerName = conn.TlsProviderName;
                    Ensure(!string.IsNullOrEmpty(tlsVersion) && (tlsVersion == "1.2" || tlsVersion == "1.3"), "TLS version below 1.2 or invalid");
                    Debug.Log($"TestTlsVersion passed: TLS {tlsVersion} (provider: {providerName})");
                    return;
                }
            }

            Debug.LogWarning("TestTlsVersion: no idle TLS connection found; unable to assert negotiated protocol");
        }

        private async Task TestUnsupportedScheme()
        {
            try
            {
                var req = new UHttpRequest(HttpMethod.GET, new Uri("ftp://example.com/"));
                await _client.SendAsync(req);
                throw new Exception("Unsupported scheme test failed: request unexpectedly succeeded");
            }
            catch (UHttpException ex)
            {
                Ensure(ex.HttpError.Type == UHttpErrorType.InvalidRequest, "Unsupported scheme test failed: wrong error type");
                Debug.Log("TestUnsupportedScheme passed");
            }
        }

        private void TestPlatformApiAvailability()
        {
            try
            {
                var enc = Encoding.GetEncoding(28591);
                Debug.Log($"Encoding.GetEncoding(28591) OK: {enc.WebName}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Encoding.GetEncoding(28591) failed: {ex.Message}");
            }

            var sslOptionsMethod = typeof(SslStream).GetMethod(
                "AuthenticateAsClientAsync",
                new[] { typeof(SslClientAuthenticationOptions), typeof(CancellationToken) });
            Debug.Log($"SslClientAuthenticationOptions overload available: {sslOptionsMethod != null}");

            try
            {
                var transport = HttpTransportFactory.Default;
                Debug.Log($"ModuleInitializer fired: {transport.GetType().Name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"ModuleInitializer check failed: {ex.Message}");
            }
        }

        private async Task MeasureLatencyBaseline()
        {
            const int count = 5;
            var total = TimeSpan.Zero;
            var beforeGc = GC.GetTotalMemory(false);

            for (int i = 0; i < count; i++)
            {
                var sw = Stopwatch.StartNew();
                var response = await _client.Get("https://httpbin.org/get").SendAsync();
                sw.Stop();

                Ensure(response.StatusCode == System.Net.HttpStatusCode.OK, "Latency baseline GET failed");
                total += sw.Elapsed;
            }

            var afterGc = GC.GetTotalMemory(false);
            var avgMs = total.TotalMilliseconds / count;
            Debug.Log($"MeasureLatencyBaseline avg: {avgMs:F1}ms, GC delta: {afterGc - beforeGc} bytes");
        }

        private static void Ensure(bool condition, string message)
        {
            if (!condition)
                throw new Exception(message);
        }
    }
}
