using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tls;
using TurboHTTP.WebSocket;
using TurboHTTP.WebSocket.Transport;

namespace TurboHTTP.Tests.WebSocket
{
    /// <summary>
    /// #368: proxy bypass, the cleartext policy and DNS see the wire host of a WebSocket target (an
    /// IDN in A-label form, RFC 5890 Section 2.3.2.1; DNS lookups use the A-label, RFC 5891 Section
    /// 5), not the Unicode <see cref="Uri.Host"/>; an IPv6 literal without brackets; ASCII names and
    /// IPv4 literals unchanged. Unicode and A-label bypass rules match either spelling.
    /// </summary>
    [TestFixture]
    public sealed class WebSocketWireHostConsumersTests
    {
        private const string Unicode = "münchen.de";
        private const string ALabel = "xn--mnchen-3ya.de";

        private Func<string, bool> _savedPlatformPolicy;

        // CleartextTrafficPolicy.PlatformPolicy is process-wide: restore it even when a test body
        // timed out and was abandoned (#349).
        [SetUp]
        public void SetUp()
        {
            _savedPlatformPolicy = CleartextTrafficPolicy.PlatformPolicy;
        }

        [TearDown]
        public void TearDown()
        {
            CleartextTrafficPolicy.PlatformPolicy = _savedPlatformPolicy;
        }

        [TestCase(ALabel, Unicode, true)]
        [TestCase(Unicode, Unicode, true)]
        [TestCase(ALabel, ALabel, true)]
        [TestCase(Unicode, ALabel, true)]
        [TestCase("MÜNCHEN.DE", ALabel, true)]
        [TestCase("*." + ALabel, "www." + Unicode, true)]
        [TestCase("*." + Unicode, "www." + Unicode, true)]
        [TestCase("*." + Unicode, "www." + ALabel, true)]
        [TestCase("*." + Unicode, Unicode, false)]
        [TestCase("*." + ALabel, Unicode, false)]
        [TestCase(" " + Unicode + " ", ALabel, true)]
        [TestCase("köln.de", Unicode, false)]
        [TestCase("Example.COM", "example.com", true)]
        [TestCase("*.example.com", "api.example.com", true)]
        [TestCase("*.example.com", "example.com", false)]
        [TestCase("10.0.0.1", "10.0.0.1", true)]
        [TestCase("::1", "::1", true)]
        public void ShouldBypass_ComparesIdnInALabelForm(string rule, string host, bool expected)
        {
            var settings = new WebSocketProxySettings(
                new Uri("http://proxy.local:8080"),
                bypassList: new[] { "", rule });

            Assert.AreEqual(expected, settings.ShouldBypass(host));
        }

        [Test]
        public void ShouldBypass_RuleWithoutALabelForm_MatchesOnlyItsOwnSpelling()
        {
            // A 70-character Unicode label has no A-label (RFC 5890 Section 2.3.2.1): the rule is kept
            // as written and never throws.
            string invalid = new string('ü', 70) + ".de";
            var settings = new WebSocketProxySettings(
                new Uri("http://proxy.local:8080"),
                bypassList: new[] { invalid });

            Assert.IsTrue(settings.ShouldBypass(invalid));
            Assert.IsFalse(settings.ShouldBypass(ALabel));
        }

        [TestCase("ws://münchen.de/", ALabel)]
        [TestCase("ws://WWW.MÜNCHEN.de:8080/", "www." + ALabel)]
        [TestCase("ws://[::1]:8080/", "::1")]
        [TestCase("ws://Example.COM/", "example.com")]
        [TestCase("ws://127.0.0.1:8080/", "127.0.0.1")]
        public void CleartextPolicy_ReceivesLookupHost_AndMessageOmitsHost(string uri, string expected)
        {
            var askedHosts = new List<string>();
            CleartextTrafficPolicy.PlatformPolicy = host =>
            {
                lock (askedHosts)
                    askedHosts.Add(host);
                return false;
            };

            using var transport = new RawSocketWebSocketTransport(
                TlsBackend.Auto, null, host => throw new AssertionException("DNS must not run: " + host));
            var options = new WebSocketConnectionOptions { AllowCleartext = true };

            // Safety bound only: the refusal happens before DNS and any socket.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => transport.ConnectAsync(new Uri(uri), options, cts.Token));

            Assert.AreEqual(WebSocketError.CleartextNotPermitted, ex.Error);
            lock (askedHosts)
                CollectionAssert.AreEqual(new[] { expected }, askedHosts);
            StringAssert.DoesNotContain(expected, ex.Message);
            StringAssert.DoesNotContain(Unicode, ex.Message);
        }

        [Test]
        public void ProxyLegCleartextRefusal_MessageOmitsProxyHost()
        {
            CleartextTrafficPolicy.PlatformPolicy = null;
            using var transport = new RawSocketWebSocketTransport(
                TlsBackend.Auto, null, host => throw new AssertionException("DNS must not run: " + host));
            var options = new WebSocketConnectionOptions
            {
                AllowCleartext = false,
                ProxySettings = new WebSocketProxySettings(new Uri("http://proxy-host.invalid:8080"))
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ex = AssertAsync.ThrowsAsync<WebSocketException>(
                () => transport.ConnectAsync(new Uri("wss://example.com/"), options, cts.Token));

            Assert.AreEqual(WebSocketError.CleartextNotPermitted, ex.Error);
            StringAssert.DoesNotContain("proxy-host", ex.Message);
        }

        [TestCase("wss://münchen.de/", ALabel)]
        [TestCase("wss://[::1]:8443/", "::1")]
        [TestCase("wss://Example.COM/", "example.com")]
        public void Dns_ReceivesLookupHost(string uri, string expected)
        {
            var resolved = new List<string>();
            using var transport = new RawSocketWebSocketTransport(
                TlsBackend.Auto,
                null,
                host =>
                {
                    lock (resolved)
                        resolved.Add(host);
                    return Task.FromException<IPAddress[]>(new ResolverStopException());
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            AssertAsync.ThrowsAsync<ResolverStopException>(
                () => transport.ConnectAsync(new Uri(uri), new WebSocketConnectionOptions(), cts.Token));

            lock (resolved)
                CollectionAssert.AreEqual(new[] { expected }, resolved);
        }

        [TestCase(ALabel)]
        [TestCase(Unicode)]
        [TestCase("*.de")]
        public void BypassRule_MatchesIdnTarget_ProxySkippedAndDnsGetsALabel(string rule)
        {
            // Bypassed: no proxy leg, so AllowCleartext = false does not refuse the http:// proxy,
            // and the target's A-label goes to DNS. Not bypassed, the proxy leg would be refused.
            CleartextTrafficPolicy.PlatformPolicy = null;
            using var proxy = new WebSocketTestProxyServer();
            var resolved = new List<string>();
            using var transport = new RawSocketWebSocketTransport(
                TlsBackend.Auto,
                null,
                host =>
                {
                    lock (resolved)
                        resolved.Add(host);
                    return Task.FromException<IPAddress[]>(new ResolverStopException());
                });
            var options = new WebSocketConnectionOptions
            {
                AllowCleartext = false,
                ProxySettings = new WebSocketProxySettings(
                    new Uri("http://127.0.0.1:" + proxy.Port),
                    bypassList: new[] { rule })
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            AssertAsync.ThrowsAsync<ResolverStopException>(
                () => transport.ConnectAsync(new Uri("wss://" + Unicode + "/"), options, cts.Token));

            lock (resolved)
                CollectionAssert.AreEqual(new[] { ALabel }, resolved);
            Assert.IsNull(proxy.LastAuthority, "the proxy must never be contacted");
        }

        [Test]
        public void IdnLocalhostSubdomain_UsesLoopbackShortcut_NotResolver()
        {
            AssertAsync.Run(async () =>
            {
                CleartextTrafficPolicy.PlatformPolicy = null;
                using var server = new WebSocketTestServer();
                var transport = new RawSocketWebSocketTransport(
                    TlsBackend.Auto,
                    null,
                    host => throw new AssertionException("a *.localhost name must not reach DNS: " + host));
                await using var client = new WebSocketClient(transport);
                var options = new WebSocketConnectionOptions
                {
                    AllowCleartext = true,
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                    PingInterval = TimeSpan.Zero
                };

                await client.ConnectAsync(
                    new Uri("ws://münchen.localhost:" + server.Port + "/echo"),
                    options,
                    CancellationToken.None).ConfigureAwait(false);
                await client.SendAsync("idn", CancellationToken.None).ConfigureAwait(false);
                using var message = await client.ReceiveAsync(CancellationToken.None).ConfigureAwait(false);

                Assert.AreEqual("idn", message.Text);
            });
        }

        private sealed class ResolverStopException : Exception
        {
        }
    }
}
