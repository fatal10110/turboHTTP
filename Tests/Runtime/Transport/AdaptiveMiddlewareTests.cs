using System;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Testing;

namespace TurboHTTP.Tests.Transport
{
    [TestFixture]
    public class AdaptiveMiddlewareTests
    {
        [Test]
        public void ColdStart_UsesBaseline()
        {
            Task.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                var transport = new MockTransport();

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    DefaultTimeout = TimeSpan.FromSeconds(10),
                    AdaptivePolicy = new AdaptivePolicy
                    {
                        Enable = true,
                        MinTimeout = TimeSpan.FromSeconds(1),
                        MaxTimeout = TimeSpan.FromSeconds(30)
                    },
                    NetworkQualityDetector = detector
                });

                var request = client.Get("https://example.test/cold").Build();

                await client.SendAsync(request);
                Assert.AreEqual(TimeSpan.FromSeconds(10), transport.LastRequest.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PoorNetwork_IncreasesTimeout()
        {
            Task.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                for (int i = 0; i < 16; i++)
                {
                    detector.AddSample(new NetworkQualitySample(
                        latencyMs: 2000,
                        totalDurationMs: 2000,
                        wasTimeout: true,
                        wasTransportFailure: true,
                        bytesTransferred: 0,
                        wasSuccess: false));
                }

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    DefaultTimeout = TimeSpan.FromSeconds(10),
                    AdaptivePolicy = new AdaptivePolicy
                    {
                        Enable = true,
                        MinTimeout = TimeSpan.FromSeconds(1),
                        MaxTimeout = TimeSpan.FromSeconds(20)
                    },
                    NetworkQualityDetector = detector
                });

                var request = client.Get("https://example.test/poor").Build();
                await client.SendAsync(request);

                Assert.Greater(transport.LastRequest.Timeout, request.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void ExplicitTimeout_NotOverridden()
        {
            Task.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                for (int i = 0; i < 16; i++)
                {
                    detector.AddSample(new NetworkQualitySample(
                        latencyMs: 1500,
                        totalDurationMs: 1500,
                        wasTimeout: true,
                        wasTransportFailure: true,
                        bytesTransferred: 0,
                        wasSuccess: false));
                }

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    AdaptivePolicy = new AdaptivePolicy
                    {
                        Enable = true,
                        MinTimeout = TimeSpan.FromSeconds(1),
                        MaxTimeout = TimeSpan.FromSeconds(20)
                    },
                    NetworkQualityDetector = detector
                });

                await client.Get("https://example.test/explicit")
                    .WithTimeout(TimeSpan.FromSeconds(3))
                    .SendAsync();

                Assert.AreEqual(TimeSpan.FromSeconds(3), transport.LastRequest.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void PoorNetwork_RespectsConfiguredMaxTimeout()
        {
            Task.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                for (int i = 0; i < 16; i++)
                {
                    detector.AddSample(new NetworkQualitySample(
                        latencyMs: 1800,
                        totalDurationMs: 1800,
                        wasTimeout: true,
                        wasTransportFailure: true,
                        bytesTransferred: 0,
                        wasSuccess: false));
                }

                var transport = new MockTransport();
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    DefaultTimeout = TimeSpan.FromSeconds(15),
                    AdaptivePolicy = new AdaptivePolicy
                    {
                        Enable = true,
                        MinTimeout = TimeSpan.FromSeconds(1),
                        MaxTimeout = TimeSpan.FromSeconds(20)
                    },
                    NetworkQualityDetector = detector
                });

                await client.Get("https://example.test/max-timeout").SendAsync();

                Assert.AreEqual(TimeSpan.FromSeconds(20), transport.LastRequest.Timeout);
            }).GetAwaiter().GetResult();
        }

        [Test]
        public void Hysteresis_PreventsThrash()
        {
            var detector = new NetworkQualityDetector(promoteAfterConsecutiveWindows: 3);

            for (int i = 0; i < 4; i++)
            {
                detector.AddSample(new NetworkQualitySample(
                    latencyMs: 950,
                    totalDurationMs: 950,
                    wasTimeout: false,
                    wasTransportFailure: false,
                    bytesTransferred: 100,
                    wasSuccess: true));
            }

            var qualityAfterFair = detector.GetSnapshot().Quality;
            Assert.AreEqual(NetworkQuality.Fair, qualityAfterFair);

            // Alternating near boundaries should not immediately promote.
            detector.AddSample(new NetworkQualitySample(150, 150, false, false, 100, true));
            detector.AddSample(new NetworkQualitySample(850, 850, false, false, 100, true));
            detector.AddSample(new NetworkQualitySample(170, 170, false, false, 100, true));

            Assert.AreEqual(NetworkQuality.Fair, detector.GetSnapshot().Quality);
        }

        [Test]
        public void Recovery_PromotesAfterKWindows()
        {
            var detector = new NetworkQualityDetector(promoteAfterConsecutiveWindows: 3);

            for (int i = 0; i < 8; i++)
            {
                detector.AddSample(new NetworkQualitySample(
                    latencyMs: 2200,
                    totalDurationMs: 2200,
                    wasTimeout: true,
                    wasTransportFailure: true,
                    bytesTransferred: 0,
                    wasSuccess: false));
            }

            Assert.AreEqual(NetworkQuality.Poor, detector.GetSnapshot().Quality);

            for (int i = 0; i < 12; i++)
            {
                detector.AddSample(new NetworkQualitySample(
                    latencyMs: 90,
                    totalDurationMs: 90,
                    wasTimeout: false,
                    wasTransportFailure: false,
                    bytesTransferred: 1024,
                    wasSuccess: true));
            }

            var recovered = detector.GetSnapshot().Quality;
            Assert.IsTrue(recovered == NetworkQuality.Good || recovered == NetworkQuality.Excellent);
        }

        [Test]
        public void NullResponseBody_DoesNotThrow()
        {
            Task.Run(async () =>
            {
                var detector = new NetworkQualityDetector();
                var transport = new MockTransport((request, context, ct) =>
                {
                    return Task.FromResult(new UHttpResponse(
                        System.Net.HttpStatusCode.OK,
                        new HttpHeaders(),
                        body: null,
                        elapsedTime: context.Elapsed,
                        request: request));
                });

                using var client = new UHttpClient(new UHttpClientOptions
                {
                    Transport = transport,
                    DisposeTransport = true,
                    AdaptivePolicy = new AdaptivePolicy { Enable = true },
                    NetworkQualityDetector = detector
                });

                var response = await client.Get("https://example.test/null-body").SendAsync();
                Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            }).GetAwaiter().GetResult();
        }
    }
}
