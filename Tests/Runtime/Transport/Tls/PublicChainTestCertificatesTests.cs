using System;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using Public = TurboHTTP.Tests.Transport.Tls.PublicChainTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #897: guards of the committed public chain. The Android TrustManager verifier evaluates at the
    /// device clock, so once the leaf expires
    /// <c>ServerCertificateVerifierTests.PublicChain_ProductionConfiguration_IsTrustedByPlatformRootStore</c>
    /// can only see <see cref="CertificateVerificationStatus.NotTimeValid"/> there. The guard fails
    /// <see cref="PublicChainTestCertificates.RenewalWindowDays"/> days before the leaf expires to leave
    /// time to re-capture the chain, and the expected verdict follows the clock the verifier used.
    /// </summary>
    [TestFixture]
    public sealed class PublicChainTestCertificatesTests
    {
        private const string Recapture =
            "re-capture the chain as PublicChainTestCertificates describes " +
            "(openssl s_client -connect www.apple.com:443 -servername www.apple.com -showcerts).";

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            // The production path: no test anchors, so Verify hands the chain to the platform verifier.
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        [Test]
        public void Constants_MatchTheCommittedChain()
        {
            using var leaf = new X509Certificate2(Public.Leaf);
            using var intermediate = new X509Certificate2(Public.Intermediate);

            Assert.AreEqual(Public.LeafNotBeforeUtc, leaf.NotBefore.ToUniversalTime(), "LeafNotBeforeUtc");
            Assert.AreEqual(Public.LeafNotAfterUtc, leaf.NotAfter.ToUniversalTime(), "LeafNotAfterUtc");
            Assert.That(Public.VerificationTimeUtc, Is.InRange(Public.LeafNotBeforeUtc, Public.LeafNotAfterUtc),
                "VerificationTimeUtc must lie inside the leaf validity");
            StringAssert.Contains("CN=" + Public.Host, leaf.Subject);
            Assert.AreEqual(intermediate.Subject, leaf.Issuer, "the intermediate issued the leaf");
            StringAssert.Contains("CN=" + Public.RootCommonName, intermediate.Issuer);
        }

        [Test]
        public void LeafNotAfter_IsMoreThan30DaysAway()
        {
            DateTime now = DateTime.UtcNow;

            Assert.LessOrEqual(Public.LeafNotBeforeUtc, now,
                "The public test chain leaf is not valid yet; check the clock or " + Recapture);
            Assert.GreaterOrEqual((Public.LeafNotAfterUtc - now).TotalDays, Public.RenewalWindowDays,
                "The public test chain leaf expires on " + Public.LeafNotAfterUtc.ToString("yyyy-MM-dd") +
                ", after which the Android verifier (device clock) can no longer trust it; " + Recapture);
        }

        [Test]
        public void ExpectedStatus_IsTrustedOnlyInsideTheLeafValidity()
        {
            var second = TimeSpan.FromSeconds(1);

            Assert.AreEqual(CertificateVerificationStatus.NotTimeValid, Public.ExpectedStatusAt(Public.LeafNotBeforeUtc - second));
            Assert.AreEqual(CertificateVerificationStatus.Trusted, Public.ExpectedStatusAt(Public.LeafNotBeforeUtc));
            Assert.AreEqual(CertificateVerificationStatus.Trusted, Public.ExpectedStatusAt(Public.VerificationTimeUtc));
            Assert.AreEqual(CertificateVerificationStatus.Trusted, Public.ExpectedStatusAt(Public.LeafNotAfterUtc));
            Assert.AreEqual(CertificateVerificationStatus.NotTimeValid, Public.ExpectedStatusAt(Public.LeafNotAfterUtc + second));
        }

        [Test]
        public void DeviceClockVerifier_ClockAfterLeafNotAfter_VerdictIsNotTimeValid()
        {
            // The Android TrustManager shape: the verifier ignores the verification time it is given
            // and evaluates the leaf at its own clock, here one day after the leaf expired.
            DateTime deviceClock = Public.LeafNotAfterUtc.AddDays(1);
            PlatformCertificateVerifier deviceClockVerifier = (chain, host, verificationTimeUtc) =>
            {
                using var leaf = new X509Certificate2(chain[0]);
                bool valid = deviceClock >= leaf.NotBefore.ToUniversalTime() && deviceClock <= leaf.NotAfter.ToUniversalTime();
                return valid
                    ? CertificateVerificationResult.Trusted(chain)
                    : CertificateVerificationResult.Rejected(CertificateVerificationStatus.NotTimeValid, "expired at the device clock");
            };

            var result = ServerCertificateVerifier.Verify(
                new[] { Public.Leaf, Public.Intermediate }, Public.Host, Public.VerificationTimeUtc,
                deviceClockVerifier, PlatformVerifierLimits.Default);

            // The fixed verification time is inside the validity, so expecting Trusted there (the
            // assertion before #897) fails; the verdict follows the clock the verifier used.
            Assert.AreEqual(CertificateVerificationStatus.Trusted, Public.ExpectedStatusAt(Public.VerificationTimeUtc));
            Assert.AreEqual(CertificateVerificationStatus.NotTimeValid, result.Status, "Detail: " + result.Detail);
            Public.AssertVerdict(result, deviceClock);
        }
    }
}
