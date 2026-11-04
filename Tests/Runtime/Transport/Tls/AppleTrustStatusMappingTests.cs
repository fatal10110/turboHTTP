using System;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #239: the SecTrustEvaluateWithError error code to result mapping shared by the iOS bridge
    /// (mirrored in <c>TurboHttpSecTrustVerifier.mm</c>) and <see cref="MacSecTrustVerifier"/>.
    /// The codes are literal SecBase.h values, so a wrong constant fails here too. Only "no trusted
    /// anchor" codes map to untrusted-root; every code is a rejection.
    /// </summary>
    [TestFixture]
    public class AppleTrustStatusMappingTests
    {
        [TestCase(-67843, AppleTrustVerifier.ResultUntrustedRoot)] // errSecNotTrusted
        [TestCase(-25318, AppleTrustVerifier.ResultUntrustedRoot)] // errSecCreateChainFailed (iOS 26 unknown root)
        [TestCase(-67818, AppleTrustVerifier.ResultNotTimeValid)] // errSecCertificateExpired
        [TestCase(-67819, AppleTrustVerifier.ResultNotTimeValid)] // errSecCertificateNotValidYet
        [TestCase(-67901, AppleTrustVerifier.ResultInvalid)] // errSecCertificateValidityPeriodTooLong
        [TestCase(-67612, AppleTrustVerifier.ResultInvalid)] // errSecInvalidRoot
        [TestCase(-67826, AppleTrustVerifier.ResultInvalid)] // errSecInvalidCertAuthority
        [TestCase(-67602, AppleTrustVerifier.ResultInvalid)] // errSecHostNameMismatch
        [TestCase(-9812, AppleTrustVerifier.ResultInvalid)] // errSSLUnknownRootCert (SecureTransport, not SecTrust)
        [TestCase(0, AppleTrustVerifier.ResultInvalid)]
        [TestCase(int.MinValue, AppleTrustVerifier.ResultInvalid)]
        [TestCase(-25318L - 0x100000000L, AppleTrustVerifier.ResultInvalid)] // truncates to -25318 as int
        [TestCase(-67843L + 0x100000000L, AppleTrustVerifier.ResultInvalid)] // truncates to -67843 as int
        public void EvaluationError_MapsToResultCode(long errorCode, int expectedCode)
        {
            Assert.AreEqual(expectedCode, AppleTrustVerifier.MapEvaluationError(errorCode));
        }

        [Test]
        public void SecBaseConstants_MatchTheHeader()
        {
            Assert.AreEqual(-25318, AppleTrustVerifier.ErrSecCreateChainFailed);
            Assert.AreEqual(-67843, AppleTrustVerifier.ErrSecNotTrusted);
            Assert.AreEqual(-67818, AppleTrustVerifier.ErrSecCertificateExpired);
            Assert.AreEqual(-67819, AppleTrustVerifier.ErrSecCertificateNotValidYet);
        }

        [Test]
        public void CreateChainFailed_RejectsAsUntrustedRoot_WithTheOsStatus()
        {
            const int osStatus = -25318;
            var result = AppleTrustVerifier.FromNative(
                AppleTrustVerifier.MapEvaluationError(osStatus), osStatus, IntPtr.Zero, 0);

            Assert.IsFalse(result.IsTrusted);
            Assert.AreEqual(CertificateVerificationStatus.UntrustedRoot, result.Status);
            Assert.IsNull(result.Path);
            StringAssert.Contains("-25318", result.Detail);
        }

        [Test]
        public void ValidityPeriodTooLong_StaysInvalid()
        {
            const int osStatus = -67901;
            var result = AppleTrustVerifier.FromNative(
                AppleTrustVerifier.MapEvaluationError(osStatus), osStatus, IntPtr.Zero, 0);

            Assert.IsFalse(result.IsTrusted);
            Assert.AreEqual(CertificateVerificationStatus.Invalid, result.Status);
            StringAssert.Contains("-67901", result.Detail);
        }
    }
}
