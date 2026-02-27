using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsPlatformCapabilitiesTests
    {
        [SetUp]
        public void ResetState()
        {
            TlsProviderSelector.ResetProbeState();
            TlsPlatformCapabilities.RefreshForTesting();
        }

        [Test]
        public void GetSummary_DoesNotThrow()
        {
            // Should return a valid struct on any supported platform.
            Assert.DoesNotThrow(() => TlsPlatformCapabilities.GetSummary());
        }

        [Test]
        public void GetSummary_IsSystemTlsAvailable_IsBoolean()
        {
            var summary = TlsPlatformCapabilities.GetSummary();
            // Just verify the field is a well-defined bool (no throw, deterministic).
            Assert.IsInstanceOf<bool>(summary.IsSystemTlsAvailable);
        }

        [Test]
        public void GetSummary_SystemProviderDescription_IsNonEmpty()
        {
            var summary = TlsPlatformCapabilities.GetSummary();
            Assert.IsNotNull(summary.SystemProviderDescription);
            Assert.IsNotEmpty(summary.SystemProviderDescription);
        }

        [Test]
        public void GetSummary_RecommendedBackend_IsValidEnum()
        {
            var summary = TlsPlatformCapabilities.GetSummary();
            Assert.That(summary.RecommendedBackend,
                Is.EqualTo(TlsBackend.Auto)
                .Or.EqualTo(TlsBackend.SslStream)
                .Or.EqualTo(TlsBackend.BouncyCastle));
        }

        [Test]
        public void GetSummary_IsCached_ReturnsSameInstance()
        {
            var s1 = TlsPlatformCapabilities.GetSummary();
            var s2 = TlsPlatformCapabilities.GetSummary();
            // Struct fields should be identical across two calls (same Lazy value).
            Assert.AreEqual(s1.IsSystemTlsAvailable, s2.IsSystemTlsAvailable);
            Assert.AreEqual(s1.SystemProviderDescription, s2.SystemProviderDescription);
            Assert.AreEqual(s1.IsBouncyCastleAvailable, s2.IsBouncyCastleAvailable);
            Assert.AreEqual(s1.RecommendedBackend, s2.RecommendedBackend);
        }

        [Test]
        public void GetDiagnosticSummary_ReturnsNonEmptyString()
        {
            var summary = TlsPlatformCapabilities.GetDiagnosticSummary();
            Assert.IsNotNull(summary);
            Assert.IsNotEmpty(summary);
            // Should contain recognizable field names for readability.
            StringAssert.Contains("SystemAvailable", summary);
            StringAssert.Contains("SystemProvider", summary);
        }

        [Test]
        public void ToString_ContainsKeyFields()
        {
            var summary = TlsPlatformCapabilities.GetSummary();
            var text = summary.ToString();
            StringAssert.Contains("SystemAvailable=", text);
            StringAssert.Contains("SystemProvider=", text);
            StringAssert.Contains("Recommended=", text);
        }

        [Test]
        public void RefreshForTesting_ClearsCache_SubsequentCallReEvaluates()
        {
            // First evaluation.
            var first = TlsPlatformCapabilities.GetSummary();

            // Refresh — the cache is invalidated.
            TlsPlatformCapabilities.RefreshForTesting();

            // Next call re-evaluates. In a stable environment the values should match.
            var second = TlsPlatformCapabilities.GetSummary();
            Assert.AreEqual(first.IsSystemTlsAvailable, second.IsSystemTlsAvailable,
                "Re-evaluated summary should match stable environment state");
        }

        [Test]
        public void GetSummary_WhenSslStreamBroken_ReflectsInSummary()
        {
            TlsProviderSelector.MarkSslStreamBroken();
            TlsPlatformCapabilities.RefreshForTesting();

            var summary = TlsPlatformCapabilities.GetSummary();
            Assert.IsTrue(summary.IsSystemTlsKnownBroken,
                "Summary should reflect the broken probe state set via MarkSslStreamBroken");

            // Recommended backend should prefer BouncyCastle when system TLS is broken.
            if (summary.IsBouncyCastleAvailable)
                Assert.AreEqual(TlsBackend.BouncyCastle, summary.RecommendedBackend);
        }

        [Test]
        public void GetSummary_BouncyCastleAvailable_MatchesSelector()
        {
            var summary = TlsPlatformCapabilities.GetSummary();
            Assert.AreEqual(TlsProviderSelector.IsBouncyCastleAvailable(),
                summary.IsBouncyCastleAvailable,
                "TlsPlatformCapabilities.IsBouncyCastleAvailable should match TlsProviderSelector");
        }
    }
}
