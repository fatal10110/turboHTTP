using System;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    [TestFixture]
    public class TlsProviderSelectorTests
    {
        [Test]
        public void ForceSslStream_ReturnsCorrectProvider()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }

        [Test]
        public void SslStream_IsAlpnSupported_ReturnsBoolean()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.SslStream);
            // Does not throw - result depends on platform
            var supported = provider.IsAlpnSupported();
            Assert.IsNotNull(supported.GetType());
        }

        [Test]
        public void Auto_ReturnsNonNullProvider()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.IsNotNull(provider);
        }

#if UNITY_EDITOR || UNITY_STANDALONE
        [Test]
        public void Auto_OnDesktop_UsesSslStream()
        {
            var provider = TlsProviderSelector.GetProvider(TlsBackend.Auto);
            Assert.AreEqual("SslStream", provider.ProviderName);
        }
#endif

        [Test]
        public void IsBouncyCastleAvailable_DoesNotThrow()
        {
            // Should not throw regardless of whether BouncyCastle is available
            bool available = TlsProviderSelector.IsBouncyCastleAvailable();
            // Just verify it returns a boolean without error
            Assert.IsInstanceOf<bool>(available);
        }

        [Test]
        public void ForceBouncyCastle_WhenNotAvailable_ThrowsInvalidOperationException()
        {
            // This test is conditional on BouncyCastle NOT being available
            if (TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is available, skipping unavailable test");
                return;
            }

            Assert.Throws<InvalidOperationException>(() =>
                TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle));
        }

        [Test]
        public void ForceBouncyCastle_WhenAvailable_ReturnsCorrectProvider()
        {
            // This test is conditional on BouncyCastle being available
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available, skipping provider test");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreEqual("BouncyCastle", provider.ProviderName);
        }

        [Test]
        public void ForceBouncyCastle_WhenAvailable_CachesInstance()
        {
            // This test verifies caching behavior
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available, skipping caching test");
                return;
            }

            var provider1 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            var provider2 = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.AreSame(provider1, provider2, "BouncyCastle provider should be cached");
        }

        [Test]
        public void BouncyCastle_WhenAvailable_IsAlpnSupported_ReturnsTrue()
        {
            if (!TlsProviderSelector.IsBouncyCastleAvailable())
            {
                Assert.Ignore("BouncyCastle is not available");
                return;
            }

            var provider = TlsProviderSelector.GetProvider(TlsBackend.BouncyCastle);
            Assert.IsTrue(provider.IsAlpnSupported(), "BouncyCastle should always support ALPN");
        }
    }
}
