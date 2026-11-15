using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #885: <see cref="AlpnAssert.RequireHttp2"/> is what makes the HTTP/2-over-TLS rows
    /// (Tests/Runtime/Integration/Http2TlsBackendRowTests) fail on an HTTP/1.1 fallback. The
    /// lenient <see cref="AlpnAssert.UsedHttp2"/> accepts that fallback where the runtime has no
    /// ALPN, so a BouncyCastle row checked with it would pass on HTTP/1.1.
    /// </summary>
    [TestFixture]
    public class AlpnAssertStrictTests
    {
        private static readonly ITlsProvider AlpnBackend = new StubTlsProvider("BouncyCastle", alpn: true);
        private static readonly ITlsProvider NoAlpnBackend = new StubTlsProvider("NoAlpn", alpn: false);

        [Test]
        public void StrictHttp2_PassesOnHttp2Connection()
        {
            Assert.IsNull(AlpnAssert.StrictHttp2Violation(AlpnBackend, usedHttp2: true, idleAlpn: null));
        }

        [TestCase(null)]
        [TestCase("http/1.1")]
        public void StrictHttp2_FailsOnHttp11Fallback(string idleAlpn)
        {
            var violation = AlpnAssert.StrictHttp2Violation(AlpnBackend, usedHttp2: false, idleAlpn: idleAlpn);

            Assert.IsNotNull(violation, "An ALPN backend that ran on HTTP/1.1 must fail the strict assert.");
            StringAssert.Contains("HTTP/1.1", violation);
            StringAssert.Contains(idleAlpn ?? "none", violation);
        }

        [Test]
        public void StrictHttp2_FailsForBackendWithoutAlpn()
        {
            // A backend that cannot offer h2 proves nothing about h2; the strict rows must not pass on it.
            var violation = AlpnAssert.StrictHttp2Violation(NoAlpnBackend, usedHttp2: false, idleAlpn: null);

            Assert.IsNotNull(violation);
            StringAssert.Contains("cannot negotiate ALPN", violation);
        }

        private sealed class StubTlsProvider : ITlsProvider
        {
            private readonly bool _alpn;

            public StubTlsProvider(string name, bool alpn)
            {
                ProviderName = name;
                _alpn = alpn;
            }

            public string ProviderName { get; }

            public Task<TlsResult> WrapAsync(Stream innerStream, string host, string[] alpnProtocols,
                CancellationToken ct) => throw new NotSupportedException();

            public bool IsAlpnSupported() => _alpn;
        }
    }
}
