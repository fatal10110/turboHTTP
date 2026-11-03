using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #46: the HTTP/1.1 Host header (and the absolute-form target) carry the A-label form of an
    /// IDN host and a single-bracketed IPv6 literal (RFC 9110 Section 7.2, RFC 9112 Section 3.2,
    /// RFC 3986 Section 3.2.2). The HTTP/2 :authority side is in Http2ConnectionTests.Authority.
    /// </summary>
    [TestFixture]
    public class HttpAuthorityTests
    {
        // Both IPv6 spellings Uri.Host has returned (.NET brackets it, Mono/Unity has not always).
        [TestCase("[::1]", "[::1]")]
        [TestCase("::1", "[::1]")]
        [TestCase("[2001:db8::5]", "[2001:db8::5]")]
        [TestCase("fe80::1%eth0", "[fe80::1]")]
        [TestCase("[fe80::1%eth0]", "[fe80::1]")]
        [TestCase("127.0.0.1", "127.0.0.1")]
        [TestCase("example.com", "example.com")]
        [TestCase("münchen.de", "xn--mnchen-3ya.de")]
        [TestCase("例え.テスト", "xn--r8jz45g.xn--zckzah")]
        public void FormatHost_ReturnsWireHost(string host, string expected)
        {
            Assert.AreEqual(expected, HttpAuthority.FormatHost(host));
        }

        [TestCase("http://münchen.de/", "xn--mnchen-3ya.de")]
        [TestCase("https://例え.テスト:8443/", "xn--r8jz45g.xn--zckzah:8443")]
        [TestCase("http://127.0.0.1/", "127.0.0.1")]
        [TestCase("http://127.0.0.1:8080/", "127.0.0.1:8080")]
        [TestCase("https://[::1]/", "[::1]")]
        [TestCase("https://[::1]:8443/", "[::1]:8443")]
        [TestCase("https://user:secret@example.com/", "example.com")]
        public void FromUri_ReturnsAuthority(string uri, string expected)
        {
            Assert.AreEqual(expected, HttpAuthority.FromUri(new Uri(uri)));
        }

        [TestCase("http://münchen.de/", "xn--mnchen-3ya.de")]
        [TestCase("http://例え.テスト:8080/", "xn--r8jz45g.xn--zckzah:8080")]
        [TestCase("http://127.0.0.1/", "127.0.0.1")]
        [TestCase("http://127.0.0.1:8080/", "127.0.0.1:8080")]
        [TestCase("http://[::1]/", "[::1]")]
        [TestCase("http://[::1]:8080/", "[::1]:8080")]
        public void Serialize_HostHeader_UsesWireAuthority(string uri, string expected)
        {
            var head = SerializeHead(new UHttpRequest(HttpMethod.GET, new Uri(uri)));

            StringAssert.Contains("\r\nHost: " + expected + "\r\n", head);
            Assert.AreEqual(1, CountOccurrences(head, "\r\nHost: "), head);
        }

        [Test]
        public void Serialize_ProxyAbsoluteForm_IdnHost_UsesALabel()
        {
            var metadata = new Dictionary<string, object> { [RequestMetadataKeys.ProxyAbsoluteForm] = true };
            var request = new UHttpRequest(
                HttpMethod.GET, new Uri("http://münchen.de:8080/a?b=1"), metadata: metadata);

            var head = SerializeHead(request);

            StringAssert.StartsWith("GET http://xn--mnchen-3ya.de:8080/a?b=1 HTTP/1.1\r\n", head);
        }

        // A 70-character label exceeds the 63-octet DNS label limit, so the name has no A-label
        // form (IdnMapping throws) and .NET's Uri.IdnHost leaves it Unicode.
        internal static readonly string UnicodeHostWithoutALabel = new string('a', 70) + "\u00FC.de";

        [Test]
        public void FormatHost_NoALabelForm_ThrowsWithoutTheHost()
        {
            var ex = Assert.Throws<HttpFieldValidationException>(
                () => HttpAuthority.FormatHost(UnicodeHostWithoutALabel));

            Assert.IsInstanceOf<ArgumentException>(ex);
            StringAssert.DoesNotContain(UnicodeHostWithoutALabel, ex.Message);
        }

        [Test]
        public void Serialize_HostWithoutALabelForm_ThrowsInsteadOfSendingRawBytes()
        {
            var uri = new Uri("http://" + UnicodeHostWithoutALabel + "/");
            if (IsAscii(uri.IdnHost))
                TestRequirements.IgnoreEnvironment("this runtime's Uri.IdnHost already produced an ASCII host");

            var request = new UHttpRequest(HttpMethod.GET, uri);

            Assert.Throws<HttpFieldValidationException>(() => HttpAuthority.FromUri(uri));
            AssertAsync.ThrowsAsync<HttpFieldValidationException>(async () =>
            {
                using var ms = new MemoryStream();
                await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);
            });
        }

        internal static bool IsAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                    return false;
            }

            return true;
        }

        private static string SerializeHead(UHttpRequest request)
        {
            byte[] bytes = null;
            AssertAsync.Run(async () =>
            {
                using var ms = new MemoryStream();
                await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);
                bytes = ms.ToArray();
            });

            for (int i = 0; i < bytes.Length; i++)
                Assert.Less(bytes[i], 0x80, "the request head must be ASCII");
            return Encoding.ASCII.GetString(bytes);
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
