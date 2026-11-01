using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TurboHTTP.Transport.Connection;

namespace TurboHTTP.Tests.Transport
{
    /// <summary>
    /// RFC 7050 prefix discovery, RFC 6052 synthesis and the IPv4-literal fallback (#167).
    /// Device behaviour on a real NAT64 network is tracked in #99 (needs-device-validation).
    /// </summary>
    [TestFixture]
    public class Nat64AddressSynthesizerTests
    {
        // RFC 6052 section 2.4, Table 1 and Table 2.
        [TestCase("2001:db8::", 32, "2001:db8:c000:221::")]
        [TestCase("2001:db8:100::", 40, "2001:db8:1c0:2:21::")]
        [TestCase("2001:db8:122::", 48, "2001:db8:122:c000:2:2100::")]
        [TestCase("2001:db8:122:300::", 56, "2001:db8:122:3c0:0:221::")]
        [TestCase("2001:db8:122:344::", 64, "2001:db8:122:344:c0:2:2100:0")]
        [TestCase("2001:db8:122:344::", 96, "2001:db8:122:344::192.0.2.33")]
        [TestCase("64:ff9b::", 96, "64:ff9b::192.0.2.33")]
        public void Synthesize_MatchesRfc6052Examples(string prefix, int length, string expected)
        {
            var nat64 = new Nat64AddressSynthesizer.Nat64Prefix(IPAddress.Parse(prefix).GetAddressBytes(), length);

            var synthesized = Nat64AddressSynthesizer.Synthesize(nat64, IPAddress.Parse("192.0.2.33"));

            Assert.AreEqual(IPAddress.Parse(expected), synthesized);
        }

        [TestCase("2001:db8::", 32)]
        [TestCase("2001:db8:100::", 40)]
        [TestCase("2001:db8:122::", 48)]
        [TestCase("2001:db8:122:300::", 56)]
        [TestCase("2001:db8:122:344::", 64)]
        [TestCase("64:ff9b::", 96)]
        public void TryExtractPrefix_FindsEveryRfc6052Format(string prefix, int length)
        {
            var expected = new Nat64AddressSynthesizer.Nat64Prefix(IPAddress.Parse(prefix).GetAddressBytes(), length);
            var answer = Nat64AddressSynthesizer.Synthesize(expected, IPAddress.Parse("192.0.0.170"));

            Assert.IsTrue(Nat64AddressSynthesizer.TryExtractPrefix(answer, out var extracted));
            Assert.AreEqual(length, extracted.Length);
            Assert.AreEqual(expected, extracted);
        }

        [Test]
        public void TryExtractPrefix_WellKnownAddressMustBeUnique()
        {
            // 192.0.0.170 at both the /32 (octets 4-7) and the /96 (octets 12-15) position and no
            // 192.0.0.171: the format is ambiguous, so no prefix (RFC 7050 section 3).
            var bytes = new byte[16];
            bytes[0] = 0x20; bytes[1] = 0x01; bytes[2] = 0x0d; bytes[3] = 0xb8;
            bytes[4] = 192; bytes[5] = 0; bytes[6] = 0; bytes[7] = 170;
            bytes[12] = 192; bytes[13] = 0; bytes[14] = 0; bytes[15] = 170;
            Assert.IsFalse(Nat64AddressSynthesizer.TryExtractPrefix(new IPAddress(bytes), out _));

            // 192.0.0.170 once (/32) and 192.0.0.171 once (/96): the first well-known address decides.
            bytes[15] = 171;
            Assert.IsTrue(Nat64AddressSynthesizer.TryExtractPrefix(new IPAddress(bytes), out var p32));
            Assert.AreEqual(32, p32.Length);
            Assert.AreEqual("2001:db8::/32", p32.ToString());

            // An answer synthesized from 192.0.0.171 alone is found by the second search.
            var only171 = Nat64AddressSynthesizer.Synthesize(
                new Nat64AddressSynthesizer.Nat64Prefix(IPAddress.Parse("64:ff9b::").GetAddressBytes(), 96),
                IPAddress.Parse("192.0.0.171"));
            Assert.IsTrue(Nat64AddressSynthesizer.TryExtractPrefix(only171, out var p96));
            Assert.AreEqual("64:ff9b::/96", p96.ToString());
        }

        [Test]
        public void TryExtractPrefix_RejectsNonSynthesizedAnswers()
        {
            Assert.IsFalse(Nat64AddressSynthesizer.TryExtractPrefix(IPAddress.Parse("192.0.0.170"), out _));
            Assert.IsFalse(Nat64AddressSynthesizer.TryExtractPrefix(IPAddress.Parse("2001:db8::1"), out _));
            // "u" octet (bits 64-71) must be zero.
            Assert.IsFalse(Nat64AddressSynthesizer.TryExtractPrefix(IPAddress.Parse("64:ff9b:0:0:100::c000:aa"), out _));
        }

        [Test]
        public void DiscoverPrefixes_ReturnsDistinctPrefixesInAnswerOrder()
        {
            AssertAsync.Run(async () =>
            {
                string queried = null;
                var prefixes = await Nat64AddressSynthesizer.DiscoverPrefixesAsync(name =>
                {
                    queried = name;
                    return Task.FromResult(new[]
                    {
                        IPAddress.Parse("192.0.0.170"),
                        IPAddress.Parse("2001:db8:122:344::c000:aa"),
                        IPAddress.Parse("64:ff9b::c000:aa"),
                        IPAddress.Parse("64:ff9b::c000:ab"),
                        IPAddress.Parse("2001:db8::99"),
                    });
                }, CancellationToken.None);

                Assert.AreEqual("ipv4only.arpa", queried);
                Assert.AreEqual(2, prefixes.Count);
                Assert.AreEqual("2001:db8:122:344::/96", prefixes[0].ToString());
                Assert.AreEqual("64:ff9b::/96", prefixes[1].ToString());
            });
        }

        [Test]
        public void DiscoverPrefixes_ResolverFailureOrIpv4OnlyAnswer_ReturnsEmpty()
        {
            AssertAsync.Run(async () =>
            {
                var failed = await Nat64AddressSynthesizer.DiscoverPrefixesAsync(
                    _ => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
                    CancellationToken.None);
                Assert.AreEqual(0, failed.Count);

                var ipv4Only = await Nat64AddressSynthesizer.DiscoverPrefixesAsync(
                    _ => Task.FromResult(new[] { IPAddress.Parse("192.0.0.170"), IPAddress.Parse("192.0.0.171") }),
                    CancellationToken.None);
                Assert.AreEqual(0, ipv4Only.Count);
            });
        }

        [Test]
        public void Ipv4Literal_NoIpv4Route_ConnectsThroughSynthesizedAddress()
        {
            AssertAsync.Run(async () =>
            {
                var attempts = new List<IPAddress[]>();
                using var socket = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    "192.0.2.33",
                    new[] { IPAddress.Parse("192.0.2.33") },
                    candidates =>
                    {
                        attempts.Add(candidates);
                        if (candidates[0].AddressFamily == AddressFamily.InterNetwork)
                            throw new SocketException((int)SocketError.NetworkUnreachable);
                        return Task.FromResult(new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp));
                    },
                    _ => Task.FromResult(new[] { IPAddress.Parse("64:ff9b::c000:aa") }),
                    CancellationToken.None);

                Assert.AreEqual(2, attempts.Count);
                Assert.AreEqual(1, attempts[1].Length);
                Assert.AreEqual(IPAddress.Parse("64:ff9b::192.0.2.33"), attempts[1][0]);
            });
        }

        [Test]
        public void Ipv4Literal_NoPrefix_RethrowsOriginalError()
        {
            var ex = AssertAsync.ThrowsAsync<SocketException>(async () =>
            {
                using var _ = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    "192.0.2.33",
                    new[] { IPAddress.Parse("192.0.2.33") },
                    _ => throw new SocketException((int)SocketError.NetworkUnreachable),
                    _ => Task.FromResult(new[] { IPAddress.Parse("192.0.0.170") }),
                    CancellationToken.None);
            });

            Assert.AreEqual(SocketError.NetworkUnreachable, ex.SocketErrorCode);
        }

        [Test]
        public void HostnameOrRefusedLiteral_NeverQueriesPrefix()
        {
            int resolverCalls = 0;
            Func<string, Task<IPAddress[]>> resolver = _ =>
            {
                resolverCalls++;
                return Task.FromResult(new[] { IPAddress.Parse("64:ff9b::c000:aa") });
            };

            AssertAsync.ThrowsAsync<SocketException>(async () =>
            {
                using var _ = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    "example.test",
                    new[] { IPAddress.Parse("192.0.2.33") },
                    _ => throw new SocketException((int)SocketError.NetworkUnreachable),
                    resolver,
                    CancellationToken.None);
            });

            AssertAsync.ThrowsAsync<SocketException>(async () =>
            {
                using var _ = await Nat64AddressSynthesizer.ConnectWithLiteralFallbackAsync(
                    "192.0.2.33",
                    new[] { IPAddress.Parse("192.0.2.33") },
                    _ => throw new SocketException((int)SocketError.ConnectionRefused),
                    resolver,
                    CancellationToken.None);
            });

            Assert.AreEqual(0, resolverCalls);
        }
    }
}
