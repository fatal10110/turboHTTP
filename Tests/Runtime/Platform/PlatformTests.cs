using NUnit.Framework;
using System;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using UnityEngine;

namespace TurboHTTP.Tests.Platform
{
    [TestFixture]
    public class PlatformTests
    {
        [Test]
        public void PlatformInfo_ReturnsExpectedValues()
        {
            Debug.Log($"[PlatformTests] Detected: {PlatformInfo.GetPlatformDescription()}");

            // Verify consistency with Unity APIs
            Assert.AreEqual(Application.platform, PlatformInfo.Platform);
            Assert.AreEqual(Application.isEditor, PlatformInfo.IsEditor);
            Assert.AreEqual(Application.unityVersion, PlatformInfo.UnityVersion);

            if (PlatformInfo.IsEditor)
            {
                Assert.IsFalse(PlatformInfo.IsMobile, "Editor should not be detected as Mobile via PlatformInfo (even if platform switch is active, Application.platform remains EditorTarget)");
                // Note: Application.platform returns e.g. OSXEditor, not OSXPlayer, even when build target is switched.
            }
        }

        [Test]
        public void PlatformConfig_ReturnsValidDefaults()
        {
            var timeout = PlatformConfig.RecommendedTimeout;
            var concurrency = PlatformConfig.RecommendedMaxConcurrency;

            Debug.Log($"[PlatformTests] Recommended Timeout: {timeout.TotalSeconds}s, Concurrency: {concurrency}");

            Assert.That(timeout.TotalSeconds, Is.GreaterThan(0));
            Assert.That(concurrency, Is.GreaterThan(0));

            // Sanity check for desktop vs mobile assumptions if we are in Editor (which mimics desktop per PlatformInfo)
            if (PlatformInfo.IsEditor)
            {
                Assert.AreEqual(30, timeout.TotalSeconds);
                Assert.AreEqual(16, concurrency);
            }
        }

        [Test]
        public void UHttpClientOptions_DefaultTimeout_UsesPlatformRecommendation()
        {
            var options = new UHttpClientOptions();
            Assert.AreEqual(
                PlatformConfig.RecommendedTimeout,
                options.DefaultTimeout,
                "UHttpClientOptions should default to platform timeout recommendation.");
        }

        [Test]
        public void RawSocketTransport_DefaultPool_UsesPlatformConcurrencyRecommendation()
        {
            using var transport = new RawSocketTransport();

            var poolField = typeof(RawSocketTransport).GetField(
                "_pool",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(poolField, "Expected RawSocketTransport to expose private _pool field.");

            var pool = (TcpConnectionPool)poolField.GetValue(transport);
            Assert.IsNotNull(pool, "Expected RawSocketTransport to initialize a TcpConnectionPool.");

            var maxConnectionsField = typeof(TcpConnectionPool).GetField(
                "_maxConnectionsPerHost",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(maxConnectionsField, "Expected TcpConnectionPool to expose private _maxConnectionsPerHost field.");

            var configuredMax = (int)maxConnectionsField.GetValue(pool);
            Assert.AreEqual(
                PlatformConfig.RecommendedMaxConcurrency,
                configuredMax,
                "RawSocketTransport should apply platform concurrency recommendation.");
        }

        [Test]
        public void IL2CPP_Compatibility_Check_Passes()
        {
            // This runs the full suite of compatibility checks defined in Core
            bool passed = IL2CPPCompatibility.Validate(out string report);
            
            Debug.Log(report);
            
            Assert.IsTrue(passed, $"IL2CPP Compatibility Checks Failed:\n{report}");
        }

        [Test]
        public void PlatformInfo_IsIL2CPP_MatchesCompileConstants()
        {
#if ENABLE_IL2CPP
            Assert.IsTrue(PlatformInfo.IsIL2CPP, "IsIL2CPP should be true when ENABLE_IL2CPP is defined.");
#else
            Assert.IsFalse(PlatformInfo.IsIL2CPP, "IsIL2CPP should be false when ENABLE_IL2CPP is NOT defined.");
#endif
        }

        [Test]
        [Category("ExternalNetwork")]
        public void NetworkReachability_External_Google_RequestSucceeds()
        {
            Task.Run(async () =>
            {
                using var client = new UHttpClient(new UHttpClientOptions
                {
                    DefaultTimeout = PlatformConfig.RecommendedTimeout,
                    Transport = new RawSocketTransport()
                });

                var candidates = new[]
                {
                    "https://httpbingo.org/status/204",
                    "https://httpbin.org/status/204",
                    "https://www.google.com/generate_204"
                };

                Exception lastError = null;
                foreach (var url in candidates)
                {
                    try
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                        var response = await client.Get(url).SendAsync(cts.Token);
                        if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK)
                            return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                Assert.Fail(
                    $"External network probe failed for all known endpoints on {PlatformInfo.GetPlatformDescription()}: " +
                    $"{lastError?.Message}");
            }).GetAwaiter().GetResult();
        }
    }
}
