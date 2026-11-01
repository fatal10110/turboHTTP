using System;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #142 (SNI part of #46): the host both providers put on the TLS wire. Callers pass
    /// <see cref="Uri.Host"/>, so IPv6 literals arrive in brackets and IDN names in Unicode;
    /// RFC 6066 section 3 wants the A-label of a DNS name and no server_name for a literal.
    /// The wire is covered by <see cref="BouncyCastleClientHelloTests"/> and
    /// <see cref="SslStreamServerNameTests"/>.
    /// </summary>
    [TestFixture]
    public class TlsServerNameTests
    {
        [TestCase("example.com", "example.com")]
        [TestCase("example.com.", "example.com")]
        [TestCase("b\u00fccher.example", "xn--bcher-kva.example")]
        [TestCase("127.0.0.1", null)]
        [TestCase("::1", null)]
        [TestCase("[::1]", null)]
        [TestCase("[2001:db8::1]", null)]
        public void ToSniHostName(string host, string expected)
        {
            Assert.AreEqual(expected, TlsHostName.ToSniHostName(host));
        }

        [TestCase("example.com", "example.com")]
        [TestCase("b\u00fccher.example", "xn--bcher-kva.example")]
        [TestCase("127.0.0.1", "127.0.0.1")]
        [TestCase("[::1]", "::1")]
        [TestCase("::1", "::1")]
        public void ToTargetHost(string host, string expected)
        {
            Assert.AreEqual(expected, TlsHostName.ToTargetHost(host));
        }
    }
}
