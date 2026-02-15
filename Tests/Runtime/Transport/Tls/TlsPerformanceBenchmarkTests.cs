#if TURBOHTTP_INTEGRATION_TESTS
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsPerformanceBenchmarkTests
    {
        [Test]
        [Category("Performance")]
        public void Benchmark_SslStream_HandshakeTime()        {
            Task.Run(async () =>
            {
                var elapsed = await MeasureHandshakeTime(TlsBackend.SslStream, iterations: 3);
                TestContext.Progress.WriteLine($"SslStream handshake benchmark (3x): {elapsed}ms");
            }).GetAwaiter().GetResult();
        }

        [Test]
        [Category("Performance")]
        public void Benchmark_BouncyCastle_HandshakeTime()        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            Task.Run(async () =>
            {
                var elapsed = await MeasureHandshakeTime(TlsBackend.BouncyCastle, iterations: 3);
                TestContext.Progress.WriteLine($"BouncyCastle handshake benchmark (3x): {elapsed}ms");
            }).GetAwaiter().GetResult();
        }

        private static async Task<long> MeasureHandshakeTime(TlsBackend backend, int iterations)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++)
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    TlsBackend = backend,
                    Transport = new RawSocketTransport()
                });

                var response = await client.Get("https://www.google.com")
                    .WithTimeout(System.TimeSpan.FromSeconds(20))
                    .SendAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            }

            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
    }
}
#endif
