using System;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;
using Chain = TurboHTTP.Tests.Transport.Tls.ChainValidationTestCertificates;
using Negative = TurboHTTP.Tests.Transport.Tls.NegativeSuiteTestCertificates;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #113: the SslStream provider's IP-literal identity check. An IP host matches only an
    /// iPAddress subjectAltName (RFC 6125 1.7.2, RFC 5280 4.2.1.6). .NET's platform check also
    /// accepted a dNSName "127.0.0.1" for host 127.0.0.1 (found by the Tools~/tls-interop job), so
    /// <c>SslStreamTlsProvider.WrapAsync</c> applies
    /// <see cref="SubjectAltNameIpAddresses.EnsureIpLiteralIdentity"/> after the handshake.
    /// </summary>
    [TestFixture]
    public class SslStreamIpIdentityTests
    {
        private const string SubjectAltNameOid = "2.5.29.17";

        [Test]
        public void Read_ReturnsTheIpAddressEntries()
        {
            using var cert = new X509Certificate2(Chain.LeafLocalhost);
            var addresses = SubjectAltNameIpAddresses.Read(cert.Extensions[SubjectAltNameOid].RawData);

            Assert.AreEqual(2, addresses.Count);
            CollectionAssert.AreEqual(new byte[] { 127, 0, 0, 1 }, addresses[0]);
            Assert.AreEqual(16, addresses[1].Length);
            Assert.AreEqual(1, addresses[1][15]);
        }

        [Test]
        public void Read_MalformedOrMissing_ReturnsNothing()
        {
            Assert.AreEqual(0, SubjectAltNameIpAddresses.Read(null).Count);
            // Truncated iPAddress entry, and a length that overruns the SEQUENCE.
            Assert.AreEqual(0, SubjectAltNameIpAddresses.Read(new byte[] { 0x30, 0x05, 0x87, 0x04, 1, 2, 3 }).Count);
            Assert.AreEqual(0, SubjectAltNameIpAddresses.Read(new byte[] { 0x30, 0x06, 0x87, 0x08, 1, 2, 3, 4 }).Count);
            // Not a SEQUENCE.
            Assert.AreEqual(0, SubjectAltNameIpAddresses.Read(new byte[] { 0x31, 0x06, 0x87, 0x04, 1, 2, 3, 4 }).Count);
        }

        [TestCase("127.0.0.1")]
        [TestCase("::1")]
        [TestCase("[::1]")]
        public void IpSanMatchingTheHost_Passes(string host)
        {
            using var cert = new X509Certificate2(Chain.LeafLocalhost);
            Assert.DoesNotThrow(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity(host, cert));
        }

        [Test]
        public void DnsSanSpellingTheAddress_Rejected()
        {
            using var cert = Negative.TryLoad(Negative.IpInDnsSan);
            if (cert == null)
            {
                TestRequirements.IgnoreEnvironment("The committed negative-suite PKCS#12 identities could not be loaded in this runtime.");
                return;
            }

            var ex = Assert.Throws<AuthenticationException>(
                () => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("127.0.0.1", cert));
            StringAssert.Contains("iPAddress", ex.Message);
        }

        [Test]
        public void OtherAddressOrNoSan_Rejected()
        {
            using var localhost = new X509Certificate2(Chain.LeafLocalhost);
            using var cnOnly = new X509Certificate2(Chain.LeafCnOnly);

            Assert.Throws<AuthenticationException>(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("10.0.0.1", localhost));
            Assert.Throws<AuthenticationException>(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("127.0.0.1", cnOnly));
            Assert.Throws<AuthenticationException>(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("127.0.0.1", null));
        }

        [Test]
        public void DnsHost_IsLeftToThePlatform()
        {
            using var cert = new X509Certificate2(Chain.LeafCnOnly);
            Assert.DoesNotThrow(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("localhost", cert));
            Assert.DoesNotThrow(() => SubjectAltNameIpAddresses.EnsureIpLiteralIdentity("wrong.example", cert));
        }
    }
}
