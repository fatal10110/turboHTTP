using System;
using System.IO;
using System.Net.Security;
using NUnit.Framework;
using TurboHTTP.Transport.Tls;

namespace TurboHTTP.Tests.Transport.Tls
{
    /// <summary>
    /// #413: the ALPN outcome against a public host that serves h2 (www.google.com).
    /// Where the runtime can negotiate ALPN, an [h2, http/1.1] offer must select h2: http/1.1 or
    /// nothing means the offer was dropped, which fails. Where it cannot, nothing can be selected
    /// (HTTP/1.1, RFC 7301 section 3.1): the provider-level h2 tests assert that and skip, because
    /// they prove nothing about h2 there. Every call logs the outcome to the test output (results
    /// XML) as device evidence.
    /// </summary>
    internal static class AlpnAssert
    {
        private static readonly Lazy<bool> s_sslStreamReportsAlpn = new Lazy<bool>(ProbeSslStreamReportsAlpn);

        /// <summary>
        /// False on Unity's Mono class library (Editor and IL2CPP players): its
        /// <c>SslStream.NegotiatedApplicationProtocol</c> always throws PlatformNotSupportedException
        /// (mono#12880) and the ALPN options are ignored, so the provider reports no ALPN (#418).
        /// Probed here independently of the provider, as the oracle for its report.
        /// </summary>
        public static bool SslStreamReportsAlpn => s_sslStreamReportsAlpn.Value;

        /// <summary>Whether <paramref name="provider"/> can really negotiate ALPN on this runtime.</summary>
        public static bool CanNegotiate(ITlsProvider provider)
        {
            if (!provider.IsAlpnSupported())
                return false;
            return provider.ProviderName != "SslStream" || SslStreamReportsAlpn;
        }

        /// <summary>
        /// Fails when this runtime's SslStream has ALPN but the SslStream provider reports none: the
        /// provider lost it (e.g. its ALPN options could not be built, #303), and the offer is dropped.
        /// </summary>
        public static void FailIfProviderLostAlpn(ITlsProvider provider)
        {
            if (provider.ProviderName == "SslStream" && SslStreamReportsAlpn && !provider.IsAlpnSupported())
                Assert.Fail("This runtime's SslStream has ALPN but the SslStream provider reports none (#303): h2 is never offered.");
        }

        /// <summary>A TLS handshake that offered h2 and http/1.1 to an h2 host.</summary>
        public static void NegotiatedH2(ITlsProvider provider, string negotiatedAlpn, string host)
        {
            FailIfProviderLostAlpn(provider);
            bool reported = provider.IsAlpnSupported();
            bool canNegotiate = CanNegotiate(provider);
            TestContext.WriteLine(
                $"ALPN {provider.ProviderName} host={host} reported={reported} runtime={canNegotiate} " +
                $"negotiated={negotiatedAlpn ?? "none"}");

            if (!canNegotiate)
            {
                Assert.IsNull(negotiatedAlpn, provider.ProviderName + " cannot negotiate ALPN here, so nothing can be selected.");
                if (reported)
                {
                    // The provider claims ALPN its runtime cannot negotiate (#418 regression).
                    Assert.Fail(provider.ProviderName +
                                " reports ALPN but this runtime's SslStream has none (mono#12880): the report must say no ALPN.");
                }

                TestRequirements.IgnoreEnvironment(
                    provider.ProviderName + " has no ALPN on this runtime; the connection uses HTTP/1.1, so h2 is unproven here.");
            }

            Assert.AreEqual("h2", negotiatedAlpn,
                $"{provider.ProviderName} can negotiate ALPN and {host} serves h2, but the handshake selected " +
                $"{negotiatedAlpn ?? "nothing"}: the ALPN offer was dropped.");
        }

        /// <summary>
        /// A request through the pool to an h2 host: HTTP/2 where the pool's provider can negotiate
        /// ALPN, otherwise the HTTP/1.1 fallback (a behaviour of its own, so this asserts it and passes).
        /// </summary>
        public static void UsedHttp2(ITlsProvider provider, string negotiatedAlpn, bool usedHttp2, string host)
        {
            FailIfProviderLostAlpn(provider);
            bool canNegotiate = CanNegotiate(provider);
            TestContext.WriteLine(
                $"ALPN {provider.ProviderName} host={host} runtime={canNegotiate} negotiated={negotiatedAlpn ?? "none"} " +
                $"http2-connection={usedHttp2}");

            if (!canNegotiate)
            {
                Assert.IsNull(negotiatedAlpn, provider.ProviderName + " cannot negotiate ALPN here, so nothing can be selected.");
                Assert.IsFalse(usedHttp2, "No ALPN result, so the request must use HTTP/1.1 (RFC 9113 section 3.2).");
                return;
            }

            Assert.AreEqual("h2", negotiatedAlpn,
                $"{provider.ProviderName} can negotiate ALPN and {host} serves h2, but the connection selected " +
                $"{negotiatedAlpn ?? "nothing"}: the ALPN offer was dropped.");
            Assert.IsTrue(usedHttp2, "ALPN selected h2 but the request did not run on an HTTP/2 connection.");
        }

        private static bool ProbeSslStreamReportsAlpn()
        {
            using var sslStream = new SslStream(new MemoryStream());
            try
            {
                _ = sslStream.NegotiatedApplicationProtocol;
                return true;
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                // A runtime with ALPN may refuse the read before a handshake; it still has the API.
                return true;
            }
        }
    }
}
