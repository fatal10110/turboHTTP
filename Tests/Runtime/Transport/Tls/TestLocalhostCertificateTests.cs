using System;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #332: guards of the committed certificate the loopback TLS tests anchor. Apple caps the
    /// validity period of a TLS server certificate issued on or after 2019-07-01 (its NotBefore) at
    /// 825 days and rejects a longer one with -67901 even when it is the test anchor (macOS hands
    /// the anchors to SecTrust), which failed every anchored BouncyCastle loopback test on a macOS
    /// player. The fixture either predates the rule or stays inside the cap, and it never expires
    /// under the suite.
    /// </summary>
    [TestFixture]
    public sealed class TestLocalhostCertificateTests
    {
        private const int AppleMaxValidityDays = 825;
        private const int RenewalWindowDays = 30;
        private const string Regenerate =
            "python3 'Tools~/test-certs/gen_localhost_cert.py' > Tests/Runtime/Transport/Tls/TestLocalhostCertificate.cs";

        private static readonly DateTime AppleValidityCapStart = new DateTime(2019, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            // TryLoad registers the certificate as a process-wide test anchor; leave the set as found.
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        [Test]
        public void ValidityPeriod_IsOneAppleTrustEvaluationAccepts()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            DateTime notBefore = certificate.NotBefore.ToUniversalTime();
            double validityDays = (certificate.NotAfter.ToUniversalTime() - notBefore).TotalDays;

            Assert.IsTrue(notBefore < AppleValidityCapStart || validityDays <= AppleMaxValidityDays,
                "Issued " + notBefore.ToString("yyyy-MM-dd") + " with a validity of " + (int)validityDays +
                " days: Apple rejects that (errSecCertificateValidityPeriodTooLong, -67901). Regenerate: " + Regenerate);
        }

        [Test]
        public void NotAfter_IsMoreThan30DaysAway()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            DateTime now = DateTime.UtcNow;

            Assert.LessOrEqual(certificate.NotBefore.ToUniversalTime(), now,
                "The certificate is not valid yet; check the clock or regenerate: " + Regenerate);
            Assert.GreaterOrEqual((certificate.NotAfter.ToUniversalTime() - now).TotalDays, RenewalWindowDays,
                "The anchored test certificate expires on " + certificate.NotAfter.ToUniversalTime().ToString("yyyy-MM-dd") +
                "; regenerate it: " + Regenerate);
        }

        [Test]
        public void Certificate_IsSelfSignedLocalhostServerWithPrivateKey()
        {
            using var certificate = LoadOrIgnore();
            if (certificate == null)
                return;

            Assert.AreEqual(certificate.Subject, certificate.Issuer, "self-signed");
            StringAssert.Contains("CN=localhost", certificate.Subject);
            Assert.IsTrue(certificate.HasPrivateKey, "the loopback TLS server needs the private key");
            Assert.IsTrue(HasServerAuthUsage(certificate), "Apple requires the serverAuth extended key usage");
        }

        private static bool HasServerAuthUsage(X509Certificate2 certificate)
        {
            const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
            foreach (var extension in certificate.Extensions)
            {
                if (!(extension is X509EnhancedKeyUsageExtension usage))
                    continue;
                foreach (var oid in usage.EnhancedKeyUsages)
                {
                    if (oid.Value == ServerAuthOid)
                        return true;
                }
            }

            return false;
        }

        private static X509Certificate2 LoadOrIgnore()
        {
            var certificate = TestLocalhostCertificate.TryLoad();
            if (certificate == null)
                TestRequirements.IgnoreEnvironment("The committed test certificate could not be loaded in this runtime.");
            return certificate;
        }
    }
}
