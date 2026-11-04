using System;
using NUnit.Framework;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #332: guards of the committed short-lived test certificate. Apple rejects a server
    /// certificate valid for more than 398 days (-67901) before its anchor check, and an expired
    /// certificate turns every "no trust anchor" case into a validity failure, so the fixture must
    /// stay inside both limits. This fails 30 days before NotAfter to leave time to regenerate.
    /// </summary>
    [TestFixture]
    public sealed class TestShortLivedLocalhostCertificateTests
    {
        private const int AppleMaxValidityDays = 398;
        private const int RenewalWindowDays = 30;
        private const string Regenerate =
            "python3 'Tools~/test-certs/gen_short_lived_localhost_cert.py' > Tests/Runtime/Transport/Tls/TestShortLivedLocalhostCertificate.cs";

        [Test]
        public void ValidityPeriod_IsWithinApplesLimit()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            TimeSpan validity = certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime();

            Assert.LessOrEqual(validity.TotalDays, AppleMaxValidityDays,
                "Apple rejects a longer validity period (errSecCertificateValidityPeriodTooLong, -67901).");
            Assert.LessOrEqual(validity.TotalDays, TestShortLivedLocalhostCertificate.ValidityDays);
        }

        [Test]
        public void NotAfter_IsMoreThan30DaysAway()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            DateTime notAfter = certificate.NotAfter.ToUniversalTime();
            DateTime notBefore = certificate.NotBefore.ToUniversalTime();
            DateTime now = DateTime.UtcNow;

            Assert.LessOrEqual(notBefore, now, "The certificate is not valid yet; check the clock or regenerate: " + Regenerate);
            Assert.GreaterOrEqual((notAfter - now).TotalDays, RenewalWindowDays,
                "The short-lived test certificate expires on " + notAfter.ToString("yyyy-MM-dd") +
                "; regenerate it: " + Regenerate);
        }

        [Test]
        public void Certificate_IsSelfSignedLocalhostWithPrivateKey()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            Assert.AreEqual(certificate.Subject, certificate.Issuer, "self-signed");
            StringAssert.Contains("CN=localhost", certificate.Subject);
            Assert.IsTrue(certificate.HasPrivateKey, "the loopback TLS server needs the private key");
        }

        private static System.Security.Cryptography.X509Certificates.X509Certificate2 LoadOrIgnore()
        {
            var certificate = TestShortLivedLocalhostCertificate.TryLoad();
            if (certificate == null)
                TestRequirements.IgnoreEnvironment("The committed short-lived test certificate could not be loaded in this runtime.");
            return certificate;
        }
    }
}
