using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Http1;
using TurboHTTP.Transport.Internal;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// #340: the wire host (Host, :authority, absolute-form, CONNECT authority) is an RFC 3986 host
    /// (Section 3.2.2) after the A-label conversion: never empty (RFC 9110 Sections 4.2.1 and
    /// 4.2.2), and an IP literal or DNS labels of 1-63 letters, digits, '-' or '_' with at most 253
    /// characters. Anything else fails as InvalidRequest before any socket opens, and the message
    /// does not contain the host. The HTTP/2 side is in Http2ConnectionTests.HostRfc3986.
    /// </summary>
    [TestFixture]
    public class HttpAuthorityRfc3986Tests
    {
        // ü + U+00A0 (no-break space): IDNA2003 maps U+00A0 to U+0020, so .NET and Unity Mono both
        // return "xn-- -dha.de" from Uri.IdnHost and IdnMapping.GetAscii.
        internal static readonly string NoBreakSpaceHost = "ü .de";

        // Unity Mono parses these as an empty host (Host == IdnHost == ""); .NET keeps the Unicode
        // name, which has no A-label form. Both must fail the same way.
        private static readonly string[] EmptyOnMonoHosts =
        {
            "-ü.de",
            new string('a', 70) + "ü.de",
        };

        private static readonly string Label63 = new string('a', 63);

        // 4 labels of 63 + 3 dots = 255; trimmed to 253 and 254 characters.
        private static readonly string Name253 = (Label63 + "." + Label63 + "." + Label63 + "." + Label63).Substring(0, 253);
        private static readonly string Name254 = (Label63 + "." + Label63 + "." + Label63 + "." + Label63).Substring(0, 254);

        private static readonly object[] ValidHosts =
        {
            new object[] { "a", "a" },
            new object[] { "my_host.local", "my_host.local" },
            new object[] { "Example-1.COM", "Example-1.COM" },
            new object[] { "example.com.", "example.com." },
            new object[] { "10.0.0.1", "10.0.0.1" },
            new object[] { "[::ffff:10.0.0.1]", "[::ffff:10.0.0.1]" },
            new object[] { Label63 + ".com", Label63 + ".com" },
            new object[] { Name253, Name253 },
            new object[] { Name253 + ".", Name253 + "." },
        };

        private static readonly object[] InvalidHosts =
        {
            new object[] { "" },
            new object[] { null },
            new object[] { "xn-- -dha.de" },
            new object[] { NoBreakSpaceHost },
            new object[] { "a b.com" },
            new object[] { "a..b" },
            new object[] { ".example.com" },
            new object[] { "example.com.." },
            new object[] { "." },
            new object[] { "ex%41mple.com" },
            new object[] { "ex!ample.com" },
            new object[] { "user@example.com" },
            new object[] { "a\tb.com" },
            new object[] { new string('a', 64) + ".com" },
            new object[] { Name254 },
            new object[] { "[example.com]" },
            new object[] { "[::1 x]" },
        };

        [TestCaseSource(nameof(ValidHosts))]
        public void FormatHost_RegNameOrIPAddress_IsSent(string host, string expected)
        {
            Assert.AreEqual(expected, HttpAuthority.FormatHost(host));
        }

        [TestCaseSource(nameof(InvalidHosts))]
        public void FormatHost_NotAnRfc3986Host_ThrowsWithoutTheHost(string host)
        {
            var ex = Assert.Throws<HttpFieldValidationException>(() => HttpAuthority.FormatHost(host));

            // Hosts of a few characters ("." for one) can occur in any sentence.
            if (host != null && host.Length > 3)
                StringAssert.DoesNotContain(host, ex.Message);
        }

        [Test]
        public void FromUri_NoBreakSpaceHost_Throws()
        {
            var uri = new Uri("http://" + NoBreakSpaceHost + "/");

            var ex = Assert.Throws<HttpFieldValidationException>(() => HttpAuthority.FromUri(uri));
            StringAssert.DoesNotContain("xn--", ex.Message);
            StringAssert.DoesNotContain(NoBreakSpaceHost, ex.Message);
        }

        [Test]
        public void FromUri_HostEmptyOnMonoOrWithoutALabel_Throws([ValueSource(nameof(EmptyOnMonoHosts))] string host)
        {
            var uri = new Uri("http://" + host + "/");

            Assert.Throws<HttpFieldValidationException>(() => HttpAuthority.FromUri(uri));
        }

        [Test]
        public void Serialize_NoBreakSpaceHost_ThrowsInsteadOfSendingTheSpace()
        {
            var request = new UHttpRequest(HttpMethod.GET, new Uri("http://" + NoBreakSpaceHost + "/"));

            AssertAsync.ThrowsAsync<HttpFieldValidationException>(async () =>
            {
                using var ms = new MemoryStream();
                await Http11RequestSerializer.SerializeAsync(request, ms, CancellationToken.None);
            });
        }

        // Through a loopback proxy so the old behavior is observable without DNS: an http:// target
        // goes out in absolute-form, an https:// target opens a CONNECT tunnel. The listener never
        // accepts; a connection the client made would be pending on it.
        [TestCase("http")]
        [TestCase("https")]
        public void Send_NoBreakSpaceHost_FailsWithInvalidRequest_BeforeAnySocket(string scheme)
        {
            AssertSendFailsBeforeAnySocket(scheme + "://" + NoBreakSpaceHost + "/x");
        }

        [Test]
        public void Send_HostEmptyOnMonoOrWithoutALabel_FailsWithInvalidRequest_BeforeAnySocket(
            [Values("http", "https")] string scheme,
            [ValueSource(nameof(EmptyOnMonoHosts))] string host)
        {
            AssertSendFailsBeforeAnySocket(scheme + "://" + host + "/x");
        }

        private static void AssertSendFailsBeforeAnySocket(string url)
        {
            AssertAsync.Run(async () =>
            {
                var proxy = new TcpListener(IPAddress.Loopback, 0);
                proxy.Start();
                try
                {
                    int proxyPort = ((IPEndPoint)proxy.LocalEndpoint).Port;
                    using var client = new UHttpClient(new UHttpClientOptions
                    {
                        Transport = new RawSocketTransport(),
                        DisposeTransport = true,
                        AllowCleartext = true,
                        DefaultTimeout = TimeSpan.FromSeconds(5),
                        Proxy = new ProxySettings
                        {
                            Address = new Uri("http://127.0.0.1:" + proxyPort),
                            UseEnvironmentVariables = false
                        }
                    });

                    var ex = AssertAsync.ThrowsAsync<UHttpException>(
                        async () => await client.Get(url).SendBufferedAsync());

                    Assert.AreEqual(UHttpErrorType.InvalidRequest, ex.HttpError.Type, ex.ToString());
                    Assert.IsFalse(ex.HttpError.IsRetryable());
                    Assert.IsInstanceOf<HttpFieldValidationException>(ex.InnerException, ex.ToString());
                    StringAssert.DoesNotContain("xn--", ex.HttpError.Message);
                    Assert.IsFalse(proxy.Pending(), "no connection is opened for an invalid host");
                }
                finally
                {
                    proxy.Stop();
                }
            });
        }
    }
}
