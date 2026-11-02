using System;
using System.Reflection;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #278: the fail-closed convention of the certificate verifiers. The zero value of every
    /// status or native result code used for a trust decision is a rejection, success has an
    /// explicit non-zero value, and <c>Trusted</c> without a path is rejected on every path
    /// (<see cref="PlatformVerifierRunner.CheckResult"/> and the BouncyCastle provider's
    /// <c>TurboTlsAuthentication.ToBuiltPath</c>, whose fatal alert the transport reports as
    /// <c>UHttpErrorType.CertificateError</c>).
    /// </summary>
    [TestFixture]
    public class VerificationFailClosedTests
    {
        private const string AuthTypeName =
            "TurboHTTP.Transport.BouncyCastle.TurboTlsAuthentication, " + TestRequirements.BouncyCastleGlueAssembly;

        private static readonly byte[] Leaf = { 0x30, 0x03, 0x02, 0x01, 0x01 };
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

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

        [Test]
        public void ZeroValues_AreRejections_AndSuccessIsNonZero()
        {
            Assert.AreEqual(0, (int)CertificateVerificationStatus.NotVerified);
            Assert.AreNotEqual(0, (int)CertificateVerificationStatus.Trusted);
            Assert.AreEqual(CertificateVerificationStatus.NotVerified, default(CertificateVerificationResult).Status);
            Assert.IsFalse(default(CertificateVerificationResult).IsTrusted);

            Assert.AreEqual(0, AppleTrustVerifier.ResultNotSet);
            Assert.AreNotEqual(0, AppleTrustVerifier.ResultTrusted);
            foreach (var code in new[]
                     {
                         AppleTrustVerifier.ResultUntrustedRoot, AppleTrustVerifier.ResultNotTimeValid,
                         AppleTrustVerifier.ResultInvalid, AppleTrustVerifier.ResultBridgeError,
                     })
            {
                Assert.AreNotEqual(AppleTrustVerifier.ResultTrusted, code);
                Assert.AreNotEqual(AppleTrustVerifier.ResultNotSet, code);
            }
        }

        [Test]
        public void DefaultResult_IsRejectedByCheckResult()
        {
            var result = PlatformVerifierRunner.CheckResult(default, Leaf);

            Assert.AreEqual(CertificateVerificationStatus.Invalid, result.Status, result.Detail);
            Assert.IsNull(result.Path);
            Assert.IsFalse(result.IsTrusted);
        }

        [Test]
        public void BridgeReturningDefault_IsRejected()
        {
            var result = ServerCertificateVerifier.Verify(new[] { Leaf }, "example.com", Now,
                (chain, host, time) => default, new PlatformVerifierLimits(TimeSpan.FromSeconds(30), 4));

            Assert.AreEqual(CertificateVerificationStatus.Invalid, result.Status, result.Detail);
            Assert.IsNull(result.Path);
        }

        [Test]
        public void TrustedWithoutPath_IsNotTrusted()
        {
            Assert.IsFalse(CertificateVerificationResult.Trusted(null).IsTrusted);
            Assert.IsFalse(CertificateVerificationResult.Trusted(new byte[0][]).IsTrusted);
            Assert.IsTrue(CertificateVerificationResult.Trusted(new[] { Leaf }).IsTrusted);
            Assert.IsFalse(CertificateVerificationResult.Rejected(CertificateVerificationStatus.Invalid, "x").IsTrusted);
        }

        [Test]
        public void Provider_DefaultResult_IsBadCertificate()
        {
            StringAssert.Contains("bad_certificate", InvokeToBuiltPathExpectingAlert(default));
        }

        [Test]
        public void Provider_TrustedWithNullPath_IsBadCertificate()
        {
            StringAssert.Contains("bad_certificate",
                InvokeToBuiltPathExpectingAlert(CertificateVerificationResult.Trusted(null)));
        }

        [Test]
        public void Provider_TrustedWithEmptyPath_IsBadCertificate()
        {
            StringAssert.Contains("bad_certificate",
                InvokeToBuiltPathExpectingAlert(CertificateVerificationResult.Trusted(new byte[0][])));
        }

        [Test]
        public void Provider_UnknownStatus_IsBadCertificate()
        {
            var unknown = new CertificateVerificationResult((CertificateVerificationStatus)99, null, new[] { Leaf });
            StringAssert.Contains("bad_certificate", InvokeToBuiltPathExpectingAlert(unknown));
        }

        [Test]
        public void Provider_TrustedWithPath_ReturnsThePath()
        {
            var path = new[] { Leaf };
            var method = ToBuiltPath();

            Assert.AreSame(path, method.Invoke(null, new object[] { CertificateVerificationResult.Trusted(path) }));
        }

        [Test]
        public void AppleBridge_NotSetCode_IsRejectedWithOsStatus()
        {
            var result = AppleTrustVerifier.FromNative(AppleTrustVerifier.ResultNotSet, -50, IntPtr.Zero, 0);

            Assert.AreEqual(CertificateVerificationStatus.Invalid, result.Status);
            Assert.IsNull(result.Path);
            StringAssert.Contains("-50", result.Detail);
        }

        [Test]
        public void WindowsBridge_ZeroedOutcome_IsRejected()
        {
            var path = new[] { Leaf };

            // A zeroed outcome: nothing built, statuses 0.
            Assert.IsFalse(WindowsCertificateVerifier.Map(false, 0, false, 0, null).IsTrusted);
            Assert.IsFalse(WindowsCertificateVerifier.Map(false, 0, true, 0, path).IsTrusted);
            // Chain clean, but the policy call failed: its zeroed status is not a pass.
            Assert.IsFalse(WindowsCertificateVerifier.Map(true, 0, false, 0, path).IsTrusted);
            // Everything passed but no path was read.
            Assert.IsFalse(WindowsCertificateVerifier.Map(true, 0, true, 0, null).IsTrusted);
            Assert.IsFalse(WindowsCertificateVerifier.Map(true, 0, true, 0, new byte[0][]).IsTrusted);

            Assert.IsTrue(WindowsCertificateVerifier.Map(true, 0, true, 0, path).IsTrusted);
        }

        [Test]
        public void NoVerifier_OnLinux_SaysBouncyCastleIsUnsupported()
        {
            var result = ServerCertificateVerifier.Verify(new[] { Leaf }, "example.com", Now, null,
                PlatformVerifierLimits.Default, verifierTrustsTestAnchors: false, VerifierPlatform.Linux);

            Assert.AreEqual(CertificateVerificationStatus.NoVerifier, result.Status);
            Assert.IsNull(result.Path);
            StringAssert.Contains("unsupported on Linux", result.Detail);
            Assert.AreEqual(ServerCertificateVerifier.NoVerifierDetail,
                ServerCertificateVerifier.NoVerifierDetailFor(VerifierPlatform.Other));
        }

        [Test]
        public void Linux_ProductionConfiguration_RejectsWithNoVerifier()
        {
            if (ServerCertificateVerifier.CurrentPlatform != VerifierPlatform.Linux)
            {
                TestRequirements.IgnoreEnvironment("Runs only in a Linux Editor or player.");
                return;
            }

            var result = ServerCertificateVerifier.Verify(new[] { Leaf }, "example.com", Now);

            Assert.AreEqual(CertificateVerificationStatus.NoVerifier, result.Status);
            StringAssert.Contains("unsupported on Linux", result.Detail);
        }

        private static MethodInfo ToBuiltPath()
        {
            var authType = TestRequirements.RequireBouncyCastleType(AuthTypeName);
            return authType.GetMethod("ToBuiltPath", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw TestRequirements.BouncyCastleMissing("TurboTlsAuthentication.ToBuiltPath not found");
        }

        private static string InvokeToBuiltPathExpectingAlert(CertificateVerificationResult result)
        {
            var method = ToBuiltPath();
            return TlsHostnameValidationTests.AssertTlsFatalAlert(() => method.Invoke(null, new object[] { result }));
        }
    }
}
