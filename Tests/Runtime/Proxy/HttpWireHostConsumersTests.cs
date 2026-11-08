using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport;
using TurboHTTP.Transport.Tcp;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Proxy
{
    /// <summary>
    /// #378 (HTTP counterpart of #368): proxy bypass matching, the cleartext policy, the connection
    /// pool and DNS see the lookup host of an HTTP target: the wire host (an IDN in A-label form, RFC
    /// 5890 Section 2.3.2.1; DNS lookups use the A-label, RFC 5891 Section 5), an IPv6 literal without
    /// brackets, ASCII names and IPv4 literals unchanged. Unicode and A-label bypass rules match either
    /// spelling. Refusal and DNS-timeout messages carry no host.
    /// </summary>
    [TestFixture]
    public sealed class HttpWireHostConsumersTests
    {
        private const string Unicode = "münchen.de";
        private const string ALabel = "xn--mnchen-3ya.de";

        private static readonly string[] ProxyVariables =
        {
            "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "NO_PROXY", "no_proxy"
        };

        private Func<string, bool> _savedPlatformPolicy;
        private Dictionary<string, string> _savedEnvironment;

        // CleartextTrafficPolicy.PlatformPolicy and the proxy variables are process-wide: restore
        // them even when a test body timed out and was abandoned (#349).
        [SetUp]
        public void SetUp()
        {
            _savedPlatformPolicy = CleartextTrafficPolicy.PlatformPolicy;
            _savedEnvironment = new Dictionary<string, string>();
            foreach (var name in ProxyVariables)
                _savedEnvironment[name] = Environment.GetEnvironmentVariable(name);
        }

        [TearDown]
        public void TearDown()
        {
            CleartextTrafficPolicy.PlatformPolicy = _savedPlatformPolicy;
            foreach (var pair in _savedEnvironment)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        [TestCase(ALabel, Unicode, 443, true)]
        [TestCase(Unicode, Unicode, 443, true)]
        [TestCase(ALabel, ALabel, 443, true)]
        [TestCase(Unicode, ALabel, 443, true)]
        [TestCase("MÜNCHEN.DE", ALabel, 443, true)]
        [TestCase(" " + Unicode + " ", ALabel, 443, true)]
        [TestCase(Unicode + ":8443", ALabel, 8443, true)]
        [TestCase(Unicode + ":8443", ALabel, 443, false)]
        [TestCase("*." + ALabel, "www." + Unicode, 443, true)]
        [TestCase("*." + Unicode, "www." + Unicode, 443, true)]
        [TestCase("*." + Unicode, "www." + ALabel, 443, true)]
        [TestCase("*." + Unicode, Unicode, 443, false)]
        [TestCase("*." + ALabel, Unicode, 443, false)]
        [TestCase("." + Unicode, ALabel, 443, true)]
        [TestCase("." + Unicode, "www." + ALabel, 443, true)]
        [TestCase("." + ALabel, "www." + Unicode, 443, true)]
        [TestCase(".de", Unicode, 443, true)]
        [TestCase("köln.de", Unicode, 443, false)]
        [TestCase("Example.COM", "example.com", 443, true)]
        [TestCase("*.example.com", "api.example.com", 443, true)]
        [TestCase("*.example.com", "example.com", 443, false)]
        [TestCase(".example.com", "example.com", 443, true)]
        [TestCase("api.test:8443", "api.test", 8443, true)]
        [TestCase("api.test:8443", "api.test", 443, false)]
        [TestCase("10.0.0.1", "10.0.0.1", 443, true)]
        [TestCase("10.0.0.0/8", "10.1.2.3", 443, true)]
        [TestCase("10.0.0.1:8080", "10.0.0.1", 443, false)]
        [TestCase("::1", "::1", 443, true)]
        [TestCase("::1", "[::1]", 443, true)]
        [TestCase("[::1]", "::1", 443, true)]
        [TestCase("[::1]:8080", "::1", 8080, true)]
        [TestCase("[::1]:8080", "::1", 443, false)]
        [TestCase("2001:db8::1", "2001:db8::1", 443, true)]
        [TestCase("2001:db8::1", "2001:db8::2", 443, false)]
        [TestCase("fd00::/8", "fd12::1", 443, true)]
        public void IsBypassed_ComparesIdnInALabelForm(string rule, string host, int port, bool expected)
        {
            Assert.AreEqual(expected, ProxyBypassMatcher.IsBypassed(host, port, new[] { "", rule }));
        }

        [Test]
        public void IsBypassed_RuleWithoutALabelForm_MatchesOnlyItsOwnSpelling()
        {
            // A 70-character Unicode label has no A-label (RFC 5890 Section 2.3.2.1): the rule is kept
            // as written and never throws.
            string invalid = new string('ü', 70) + ".de";

            Assert.IsTrue(ProxyBypassMatcher.IsBypassed(invalid, 443, new[] { invalid }));
            Assert.IsFalse(ProxyBypassMatcher.IsBypassed(ALabel, 443, new[] { invalid }));
        }

        [TestCase(Unicode, "https://" + ALabel + "/")]
        [TestCase(ALabel, "https://" + Unicode + "/")]
        [TestCase("*.de", "https://www." + Unicode + "/")]
        public void Resolve_ConfiguredBypassRule_MatchesIdnTarget(string rule, string target)
        {
            var configured = new ProxySettings
            {
                Address = new Uri("http://proxy.example:8080"),
                BypassList = new[] { rule },
                UseEnvironmentVariables = false
            };

            Assert.IsNull(ProxyEnvironmentResolver.Resolve(new Uri(target), configured));
            Assert.IsNotNull(ProxyEnvironmentResolver.Resolve(new Uri("https://example.org/"), configured));
        }

        [TestCase(Unicode, "http://" + ALabel + "/")]
        [TestCase(ALabel, "http://" + Unicode + "/")]
        [TestCase("other.example, ." + Unicode, "http://api." + Unicode + "/")]
        public void Resolve_NoProxyRule_MatchesIdnTarget(string noProxy, string target)
        {
            foreach (var name in ProxyVariables)
                Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable("HTTP_PROXY", "http://env-proxy.example:3128");
            Environment.SetEnvironmentVariable("NO_PROXY", noProxy);

            Assert.IsNull(ProxyEnvironmentResolver.Resolve(new Uri(target), null));
            Assert.IsNotNull(ProxyEnvironmentResolver.Resolve(new Uri("http://example.org/"), null));
        }

        [TestCase("http://münchen.de/", ALabel)]
        [TestCase("http://WWW.MÜNCHEN.de:8080/", "www." + ALabel)]
        [TestCase("http://[::1]:8080/", "::1")]
        [TestCase("http://Example.COM/", "example.com")]
        [TestCase("http://127.0.0.1:8080/", "127.0.0.1")]
        public void CleartextPolicy_ReceivesLookupHost_AndMessageOmitsHost(string uri, string expected)
        {
            var askedHosts = new List<string>();
            CleartextTrafficPolicy.PlatformPolicy = host =>
            {
                lock (askedHosts)
                    askedHosts.Add(host);
                return false;
            };
            var resolved = new List<string>();
            using var client = CreateClient(RecordingResolver(resolved), allowCleartext: true, proxy: null);

            var ex = SendAndCatch(client, uri);

            var httpError = ((UHttpException)ex).HttpError;
            Assert.AreEqual(UHttpErrorType.InvalidRequest, httpError.Type, ex.ToString());
            lock (askedHosts)
                CollectionAssert.AreEqual(new[] { expected }, askedHosts);
            lock (resolved)
                CollectionAssert.IsEmpty(resolved, "the refusal happens before DNS");
            StringAssert.DoesNotContain(expected, httpError.Message);
            StringAssert.DoesNotContain(Unicode, httpError.Message);
        }

        [Test]
        public void ProxyLegCleartextPolicy_ReceivesProxyALabel_AndMessageOmitsProxyHost()
        {
            var askedHosts = new List<string>();
            CleartextTrafficPolicy.PlatformPolicy = host =>
            {
                lock (askedHosts)
                    askedHosts.Add(host);
                return false;
            };
            var resolved = new List<string>();
            var proxy = new ProxySettings
            {
                Address = new Uri("http://" + Unicode + ":8080"),
                UseEnvironmentVariables = false
            };
            using var client = CreateClient(RecordingResolver(resolved), allowCleartext: true, proxy);

            var ex = SendAndCatch(client, "https://example.com/");

            var httpError = ((UHttpException)ex).HttpError;
            Assert.AreEqual(UHttpErrorType.InvalidRequest, httpError.Type, ex.ToString());
            StringAssert.Contains("proxy", httpError.Message);
            lock (askedHosts)
                CollectionAssert.AreEqual(new[] { ALabel }, askedHosts);
            lock (resolved)
                CollectionAssert.IsEmpty(resolved, "the refusal happens before DNS");
            StringAssert.DoesNotContain(ALabel, httpError.Message);
            StringAssert.DoesNotContain(Unicode, httpError.Message);
        }

        [Test]
        public void ProxyLegRefusedByAllowCleartext_MessageOmitsProxyHost()
        {
            CleartextTrafficPolicy.PlatformPolicy = null;
            var proxy = new ProxySettings
            {
                Address = new Uri("http://proxy-host.invalid:8080"),
                UseEnvironmentVariables = false
            };
            using var client = CreateClient(RecordingResolver(new List<string>()), allowCleartext: false, proxy);

            var ex = SendAndCatch(client, "https://example.com/");

            var httpError = ((UHttpException)ex).HttpError;
            Assert.AreEqual(UHttpErrorType.InvalidRequest, httpError.Type, ex.ToString());
            StringAssert.DoesNotContain("proxy-host", httpError.Message);
        }

        [TestCase("https://münchen.de/", ALabel)]
        [TestCase("https://WWW.MÜNCHEN.de:8443/", "www." + ALabel)]
        [TestCase("https://[2001:db8::1]:8443/", "2001:db8::1")]
        [TestCase("https://Example.COM/", "example.com")]
        [TestCase("https://192.0.2.1/", "192.0.2.1")]
        public void Dns_ReceivesLookupHost(string uri, string expected)
        {
            CleartextTrafficPolicy.PlatformPolicy = null;
            var resolved = new List<string>();
            using var client = CreateClient(RecordingResolver(resolved), allowCleartext: false, proxy: null);

            SendAndCatch(client, uri);

            lock (resolved)
                CollectionAssert.AreEqual(new[] { expected }, resolved);
        }

        [Test]
        public void Dns_ThroughIdnProxy_ReceivesProxyALabel()
        {
            CleartextTrafficPolicy.PlatformPolicy = null;
            var resolved = new List<string>();
            var proxy = new ProxySettings
            {
                Address = new Uri("http://" + Unicode + ":8080"),
                UseEnvironmentVariables = false
            };
            using var client = CreateClient(RecordingResolver(resolved), allowCleartext: true, proxy);

            SendAndCatch(client, "http://example.com/");

            lock (resolved)
                CollectionAssert.AreEqual(new[] { ALabel }, resolved);
        }

        [TestCase(ALabel)]
        [TestCase(Unicode)]
        [TestCase("*.de")]
        [TestCase(".de")]
        public void BypassRule_MatchesIdnTarget_ProxySkippedAndDnsGetsALabel(string rule)
        {
            // Bypassed: no proxy leg, so AllowCleartext = false does not refuse the http:// proxy,
            // and the target's A-label goes to DNS. Not bypassed, the proxy leg would be refused.
            CleartextTrafficPolicy.PlatformPolicy = null;
            var resolved = new List<string>();
            var proxy = new ProxySettings
            {
                Address = new Uri("http://proxy-host.invalid:8080"),
                BypassList = new[] { rule },
                UseEnvironmentVariables = false
            };
            using var client = CreateClient(RecordingResolver(resolved), allowCleartext: false, proxy);

            SendAndCatch(client, "https://" + Unicode + "/");

            lock (resolved)
                CollectionAssert.AreEqual(new[] { ALabel }, resolved);
        }

        [Test]
        public void DnsTimeout_MessageOmitsHost()
        {
            CleartextTrafficPolicy.PlatformPolicy = null;
            var pending = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                // The resolver never answers, so the pool's DNS timeout always fires first.
                var pool = new TcpConnectionPool(TlsBackend.Auto, TimeSpan.FromMilliseconds(50), _ => pending.Task);
                using var client = CreateClient(pool, allowCleartext: false, proxy: null);

                var ex = SendAndCatch(client, "https://" + Unicode + "/");

                var httpError = ((UHttpException)ex).HttpError;
                Assert.AreEqual(UHttpErrorType.Timeout, httpError.Type, ex.ToString());
                StringAssert.Contains("DNS", httpError.Message);
                StringAssert.DoesNotContain(ALabel, httpError.Message);
                StringAssert.DoesNotContain(Unicode, httpError.Message);
            }
            finally
            {
                pending.TrySetResult(Array.Empty<IPAddress>());
            }
        }

        private static TcpConnectionPool RecordingResolver(List<string> resolved)
        {
            return new TcpConnectionPool(
                TlsBackend.Auto,
                dnsTimeout: null,
                host =>
                {
                    lock (resolved)
                        resolved.Add(host);
                    return Task.FromException<IPAddress[]>(new ResolverStopException());
                });
        }

        private static UHttpClient CreateClient(TcpConnectionPool pool, bool allowCleartext, ProxySettings proxy)
        {
            return new UHttpClient(new UHttpClientOptions
            {
                Transport = new RawSocketTransport(pool),
                DisposeTransport = true,
                AllowCleartext = allowCleartext,
                // Safety bound only: every request here fails before or at DNS.
                DefaultTimeout = TimeSpan.FromSeconds(10),
                Proxy = proxy ?? new ProxySettings { UseEnvironmentVariables = false }
            });
        }

        private static Exception SendAndCatch(UHttpClient client, string uri)
        {
            Exception caught = null;
            AssertAsync.Run(async () =>
            {
                try
                {
                    using var response = await client.Get(uri).SendBufferedAsync();
                }
                catch (AssertionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });

            Assert.IsNotNull(caught, "the request must fail before any connection");
            return caught;
        }

        private sealed class ResolverStopException : Exception
        {
        }
    }
}
