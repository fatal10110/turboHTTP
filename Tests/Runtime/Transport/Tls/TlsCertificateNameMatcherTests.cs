using System;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #33: RFC 6125 section 6 server identity rules of <see cref="TlsCertificateNameMatcher"/>
    /// (used by the BouncyCastle provider).
    /// </summary>
    [TestFixture]
    public class TlsCertificateNameMatcherTests
    {
        private static readonly byte[] Loopback4 = { 127, 0, 0, 1 };
        private static readonly byte[] Loopback6 = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 };

        private static bool MatchDns(string host, params string[] dnsNames)
            => TlsCertificateNameMatcher.Matches(host, true, dnsNames, Array.Empty<byte[]>(), Array.Empty<string>());

        [TestCase("example.com", "example.com")]
        [TestCase("EXAMPLE.com", "example.COM")]
        [TestCase("api.example.com", "*.example.com")]
        [TestCase("example.com.", "example.com")]
        [TestCase("bücher.example", "xn--bcher-kva.example")]
        [TestCase("API.Example.com.", "*.example.com")]
        public void DnsName_Matches(string host, string pattern)
        {
            Assert.IsTrue(MatchDns(host, pattern));
        }

        [TestCase("example.com", "*.com", TestName = "DnsName_TldWildcard_Rejected")]
        [TestCase("example.com", "*.example.com", TestName = "DnsName_WildcardNeedsALabel_Rejected")]
        [TestCase("a.b.example.com", "*.example.com", TestName = "DnsName_WildcardSpansOneLabel_Rejected")]
        [TestCase("foo.example.com", "f*.example.com", TestName = "DnsName_PartialWildcard_Rejected")]
        [TestCase("foo.example.com", "*o.example.com", TestName = "DnsName_SuffixWildcard_Rejected")]
        [TestCase("www.foo.com", "www.*.com", TestName = "DnsName_InnerWildcard_Rejected")]
        [TestCase("x.example.com", "*.*.com", TestName = "DnsName_DoubleWildcard_Rejected")]
        [TestCase("attacker.com", "example.com", TestName = "DnsName_OtherHost_Rejected")]
        [TestCase("example.com", "example.com.evil", TestName = "DnsName_Suffix_Rejected")]
        [TestCase("bücher.example", "bücher.example", TestName = "DnsName_UnicodePattern_Rejected")]
        [TestCase("example.com..", "example.com", TestName = "DnsName_TwoTrailingDots_Rejected")]
        [TestCase("127.0.0.1", "127.0.0.1", TestName = "IpHost_NeverMatchesDnsName")]
        [TestCase("[::1]", "::1", TestName = "Ipv6Host_NeverMatchesDnsName")]
        public void DnsName_DoesNotMatch(string host, string pattern)
        {
            Assert.IsFalse(MatchDns(host, pattern));
        }

        [Test]
        public void IpHost_MatchesIpAddressEntry()
        {
            Assert.IsTrue(TlsCertificateNameMatcher.Matches(
                "127.0.0.1", true, Array.Empty<string>(), new[] { Loopback4 }, Array.Empty<string>()));
            Assert.IsTrue(TlsCertificateNameMatcher.Matches(
                "[::1]", true, Array.Empty<string>(), new[] { Loopback6 }, Array.Empty<string>()));
            Assert.IsTrue(TlsCertificateNameMatcher.Matches(
                "::1", true, Array.Empty<string>(), new[] { Loopback6 }, Array.Empty<string>()));
        }

        [Test]
        public void IpHost_DifferentAddressOrFamily_Rejected()
        {
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "127.0.0.2", true, Array.Empty<string>(), new[] { Loopback4 }, Array.Empty<string>()));
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "[::1]", true, Array.Empty<string>(), new[] { Loopback4 }, Array.Empty<string>()));
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "127.0.0.1", true, Array.Empty<string>(), new[] { Loopback6 }, Array.Empty<string>()));
        }

        [Test]
        public void IpHost_NeverMatchesCommonName()
        {
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "127.0.0.1", false, Array.Empty<string>(), Array.Empty<byte[]>(), new[] { "127.0.0.1" }));
        }

        [Test]
        public void DnsHost_NeverMatchesIpAddressEntry()
        {
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "localhost", true, Array.Empty<string>(), new[] { Loopback4 }, Array.Empty<string>()));
        }

        [Test]
        public void CommonName_UsedOnlyWithoutSubjectAltName()
        {
            Assert.IsTrue(TlsCertificateNameMatcher.Matches(
                "example.com", false, Array.Empty<string>(), Array.Empty<byte[]>(), new[] { "example.com" }));
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "example.com", true, new[] { "other.example" }, Array.Empty<byte[]>(), new[] { "example.com" }),
                "A subjectAltName extension disables the CN fallback (RFC 6125 6.4.4).");
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "example.com", true, Array.Empty<string>(), new[] { Loopback4 }, new[] { "example.com" }),
                "An iPAddress-only subjectAltName also disables the CN fallback.");
        }

        [Test]
        public void CommonName_SeveralValues_Rejected()
        {
            Assert.IsFalse(TlsCertificateNameMatcher.Matches(
                "example.com", false, Array.Empty<string>(), Array.Empty<byte[]>(), new[] { "other.example", "example.com" }));
        }

        [Test]
        public void EmptyHost_Rejected()
        {
            Assert.IsFalse(MatchDns("", "example.com"));
            Assert.IsFalse(MatchDns(null, "example.com"));
        }
    }
}
