using System;
using NUnit.Framework;
using Certs = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #929: the BouncyCastle host check never falls back to the subject CN. Server identity comes
    /// from subjectAltName only (RFC 9525 section 6), as Android's HostnameVerifier and Apple's SSL
    /// policy require (stricter than Windows' crypt32 SSL policy, which still falls back to the CN),
    /// so a leaf without a subjectAltName extension is rejected for every host, including the one
    /// its CN names. The end-to-end handshake case is
    /// the <c>cn-only</c> row of <see cref="TlsNegativeSuiteTests"/>.
    /// </summary>
    [TestFixture]
    public class BouncyCastleCnFallbackTests
    {
        private static readonly DateTime Now = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        [TestCase("example.com", TestName = "CnOnlyLeaf_CnHost_Rejected")]
        [TestCase("EXAMPLE.com.", TestName = "CnOnlyLeaf_CnHostCaseAndTrailingDot_Rejected")]
        [TestCase("www.example.com", TestName = "CnOnlyLeaf_Subdomain_Rejected")]
        [TestCase("127.0.0.1", TestName = "CnOnlyLeaf_IpHost_Rejected")]
        public void CnOnlyLeaf_Rejected(string host)
        {
            var message = TlsHostnameValidationTests.AssertTlsFatalAlert(
                () => TlsHostnameValidationTests.InvokeValidateLeaf(Certs.LeafCnOnly, host, Now));

            StringAssert.Contains("bad_certificate", message);
            StringAssert.Contains("no subjectAltName", message);
        }

        [Test]
        public void CnWithOtherSan_CnHost_Rejected()
        {
            var message = TlsHostnameValidationTests.AssertTlsFatalAlert(
                () => TlsHostnameValidationTests.InvokeValidateLeaf(Certs.LeafCnWithSan, "example.com", Now));

            StringAssert.Contains("bad_certificate", message);
            StringAssert.Contains("does not match host", message);
        }
    }
}
