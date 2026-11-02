using System;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using Certs = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;
using Win = TurboHTTP.Transport.Tls.WindowsCertificateVerifier;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #248: the native desktop verifiers. The status mappings run everywhere; the cases that call
    /// crypt32 or SecTrust run only on that host OS (the macOS ones need a Mac:
    /// needs-device-validation). Path-validation cases on committed chains are in
    /// <see cref="ServerCertificateVerifierTests"/>, which runs the production configuration.
    /// </summary>
    [TestFixture]
    public class DesktopCertificateVerifierTests
    {
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly byte[][] Path = { new byte[] { 0x30, 0x03, 0x02, 0x01, 0x01 } };

        private byte[][] _savedAnchors;

        [SetUp]
        public void SetUp()
        {
            _savedAnchors = ServerCertificateVerifier.SnapshotTestTrustAnchors();
            ServerCertificateVerifier.RestoreTestTrustAnchors(null);
        }

        [TearDown]
        public void TearDown()
        {
            ServerCertificateVerifier.RestoreTestTrustAnchors(_savedAnchors);
        }

        private static void AssertRejected(CertificateVerificationStatus expected, CertificateVerificationResult result)
        {
            Assert.AreEqual(expected, result.Status, "Detail: " + result.Detail);
            Assert.IsNull(result.Path, "a rejection carries no path");
        }

        // Test parameters are ints/uints: the status enum is internal and NUnit test methods are public.
        [TestCase(Win.TrustIsUntrustedRoot, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.TrustIsPartialChain, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.TrustIsUntrustedRoot | Win.TrustIsNotTimeValid, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.TrustIsUntrustedRoot | Win.TrustIsNotSignatureValid, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.TrustIsNotTimeValid, (int)CertificateVerificationStatus.NotTimeValid)]
        [TestCase(Win.TrustIsNotTimeValid | Win.TrustIsNotTimeNested, (int)CertificateVerificationStatus.NotTimeValid)]
        [TestCase(Win.TrustIsNotTimeValid | Win.TrustIsNotValidForUsage, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustIsNotValidForUsage, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustIsNotSignatureValid, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustIsRevoked, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustHasExcludedNameConstraint, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustHasNotPermittedNameConstraint, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(Win.TrustIsOfflineRevocation | Win.TrustIsNotTimeValid, (int)CertificateVerificationStatus.NotTimeValid)]
        public void Windows_ChainStatus_MapsLikeEveryVerifier(uint errorStatus, int expectedValue)
        {
            AssertRejected((CertificateVerificationStatus)expectedValue, Win.Map(true, errorStatus, true, 0, Path));
        }

        [Test]
        public void Windows_RevocationUnknownOnly_IsNotAPathError()
        {
            // Revocation is off by decision A; the bits alone do not reject.
            var result = Win.Map(true, Win.TrustRevocationStatusUnknown | Win.TrustIsOfflineRevocation, true, 0, Path);

            Assert.IsTrue(result.IsTrusted, result.Detail);
        }

        [TestCase(Win.CertEUntrustedRoot, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.CertEChaining, (int)CertificateVerificationStatus.UntrustedRoot)]
        [TestCase(Win.CertEExpired, (int)CertificateVerificationStatus.NotTimeValid)]
        [TestCase(Win.CertECnNoMatch, (int)CertificateVerificationStatus.Invalid)]
        [TestCase(0x80092010u, (int)CertificateVerificationStatus.Invalid)]
        public void Windows_PolicyError_Maps(uint policyError, int expectedValue)
        {
            AssertRejected((CertificateVerificationStatus)expectedValue, Win.Map(true, 0, true, policyError, Path));
        }

        [TestCase(AppleTrustVerifier.ErrSecNotTrusted, AppleTrustVerifier.ResultUntrustedRoot)]
        [TestCase(AppleTrustVerifier.ErrSecCertificateExpired, AppleTrustVerifier.ResultNotTimeValid)]
        [TestCase(AppleTrustVerifier.ErrSecCertificateNotValidYet, AppleTrustVerifier.ResultNotTimeValid)]
        [TestCase(-67602, AppleTrustVerifier.ResultInvalid)]
        [TestCase(0, AppleTrustVerifier.ResultInvalid)]
        public void MacOS_EvaluationError_MapsLikeTheIosBridge(long errorCode, int expectedCode)
        {
            Assert.AreEqual(expectedCode, AppleTrustVerifier.MapEvaluationError(errorCode));
        }

        [Test]
        public void ProductionSelection_UsesTheHostVerifier()
        {
            switch (ServerCertificateVerifier.CurrentPlatform)
            {
                case VerifierPlatform.Windows:
                case VerifierPlatform.MacOS:
                    Assert.IsTrue(ServerCertificateVerifier.PlatformVerifierTrustsTestAnchors);
                    break;
                default:
                    Assert.IsFalse(ServerCertificateVerifier.PlatformVerifierTrustsTestAnchors);
                    break;
            }
        }

        [Test]
        public void DesktopVerifier_AppliesThePlatformHostPolicy()
        {
            RequireDesktopVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            // The managed RFC 6125 matcher runs before this in the provider; the platform policy
            // rejects a leaf for another host on its own as well.
            var result = ServerCertificateVerifier.Verify(new[] { Certs.LeafLocalhost }, "example.com", Now);

            AssertRejected(CertificateVerificationStatus.Invalid, result);
        }

        [Test]
        public void DesktopVerifier_IpLiteralHost_MatchesIpAddressSan()
        {
            RequireDesktopVerifier();
            ServerCertificateVerifier.AddTestTrustAnchor(Certs.RootA);

            var result = ServerCertificateVerifier.Verify(new[] { Certs.LeafLocalhost }, "127.0.0.1", Now);

            Assert.AreEqual(CertificateVerificationStatus.Trusted, result.Status, "Detail: " + result.Detail);
        }

        [Test]
        public void Windows_TestAnchorsAreTheOnlyRoots()
        {
            if (ServerCertificateVerifier.CurrentPlatform != VerifierPlatform.Windows)
            {
                TestRequirements.IgnoreEnvironment("Calls crypt32; runs only on Windows.");
                return;
            }

            var trusted = Win.Evaluate(new[] { Certs.LeafP384 }, "localhost", Now, new[] { Certs.RootB });
            Assert.IsTrue(trusted.IsTrusted, trusted.Detail);
            Assert.AreEqual(2, trusted.Path.Length, "leaf + anchor");
            CollectionAssert.AreEqual(Certs.RootB, trusted.Path[1]);

            AssertRejected(CertificateVerificationStatus.UntrustedRoot,
                Win.Evaluate(new[] { Certs.LeafP384 }, "localhost", Now, new[] { Certs.RootA }));
            AssertRejected(CertificateVerificationStatus.Invalid,
                Win.Evaluate(new[] { new byte[] { 0x30, 0x03, 0x02, 0x01, 0x00 } }, "localhost", Now, new[] { Certs.RootA }));
        }

        private static void RequireDesktopVerifier()
        {
            if (!ServerCertificateVerifier.PlatformVerifierTrustsTestAnchors)
                TestRequirements.IgnoreEnvironment("Needs the Windows or macOS desktop verifier.");
        }
    }
}
