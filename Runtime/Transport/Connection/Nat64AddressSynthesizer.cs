using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Connection
{
    /// <summary>
    /// NAT64 support for IPv4 address literals on IPv6-only networks (RFC 8305 section 7.1):
    /// discovers the Pref64::/n through the well-known name <c>ipv4only.arpa</c> (RFC 7050 section 3)
    /// and synthesizes IPv4-embedded IPv6 addresses (RFC 6052 section 2.2).
    /// </summary>
    /// <remarks>
    /// Hostnames do not need this: the platform resolver (getaddrinfo with DNS64) already returns
    /// synthesized AAAA records. An IPv4 literal never reaches the resolver, so it is first tried
    /// as-is; only when that fails because the host has no IPv4 route is the prefix discovered and
    /// the synthesized addresses raced instead. Dual-stack and IPv4 networks therefore pay nothing.
    /// </remarks>
    internal static class Nat64AddressSynthesizer
    {
        /// <summary>The RFC 7050 well-known IPv4-only name.</summary>
        internal const string WellKnownName = "ipv4only.arpa";

        // RFC 7050 section 2.2: the well-known IPv4 addresses of ipv4only.arpa, searched in this order.
        private static readonly byte[][] WellKnownIpv4 =
        {
            new byte[] { 192, 0, 0, 170 },
            new byte[] { 192, 0, 0, 171 },
        };

        // RFC 6052 section 2.2: valid prefix lengths and the octets that carry the IPv4 address
        // (octet 8, the "u" octet, is always zero and never carries address bits).
        private static readonly int[] PrefixLengths = { 32, 40, 48, 56, 64, 96 };

        private static readonly int[][] Ipv4Octets =
        {
            new[] { 4, 5, 6, 7 },
            new[] { 5, 6, 7, 9 },
            new[] { 6, 7, 9, 10 },
            new[] { 7, 9, 10, 11 },
            new[] { 9, 10, 11, 12 },
            new[] { 12, 13, 14, 15 },
        };

        /// <summary>A discovered Pref64::/n.</summary>
        internal readonly struct Nat64Prefix : IEquatable<Nat64Prefix>
        {
            internal Nat64Prefix(byte[] prefixBytes, int length)
            {
                PrefixBytes = prefixBytes;
                Length = length;
            }

            /// <summary>16 bytes; every bit after <see cref="Length"/> is zero.</summary>
            internal byte[] PrefixBytes { get; }

            /// <summary>Prefix length in bits: 32, 40, 48, 56, 64 or 96.</summary>
            internal int Length { get; }

            public bool Equals(Nat64Prefix other)
            {
                if (Length != other.Length)
                    return false;
                for (int i = 0; i < 16; i++)
                {
                    if (PrefixBytes[i] != other.PrefixBytes[i])
                        return false;
                }

                return true;
            }

            public override bool Equals(object obj) => obj is Nat64Prefix other && Equals(other);

            public override int GetHashCode() => Length ^ (PrefixBytes[0] << 8) ^ PrefixBytes[1];

            public override string ToString() => new IPAddress(PrefixBytes) + "/" + Length;
        }

        /// <summary>
        /// Extracts the Pref64::/n from one AAAA answer for <see cref="WellKnownName"/>
        /// (RFC 7050 section 3). The well-known IPv4 address must appear at exactly one RFC 6052
        /// position; when 192.0.0.170 is ambiguous the search repeats with 192.0.0.171.
        /// </summary>
        internal static bool TryExtractPrefix(IPAddress synthesized, out Nat64Prefix prefix)
        {
            prefix = default;
            if (synthesized == null || synthesized.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            var bytes = synthesized.GetAddressBytes();
            if (bytes[8] != 0)
                return false; // RFC 6052 section 2.2: bits 64-71 MUST be zero in every format.

            for (int w = 0; w < WellKnownIpv4.Length; w++)
            {
                int found = -1;
                int matches = 0;
                for (int f = 0; f < PrefixLengths.Length; f++)
                {
                    if (Matches(bytes, Ipv4Octets[f], WellKnownIpv4[w]))
                    {
                        matches++;
                        found = f;
                    }
                }

                if (matches != 1)
                    continue;

                int length = PrefixLengths[found];
                var prefixBytes = new byte[16];
                Buffer.BlockCopy(bytes, 0, prefixBytes, 0, length / 8);
                prefix = new Nat64Prefix(prefixBytes, length);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Builds the IPv4-embedded IPv6 address for <paramref name="ipv4"/> under
        /// <paramref name="prefix"/> (RFC 6052 section 2.2; "u" octet and suffix zero).
        /// </summary>
        internal static IPAddress Synthesize(Nat64Prefix prefix, IPAddress ipv4)
        {
            if (ipv4 == null) throw new ArgumentNullException(nameof(ipv4));
            if (ipv4.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("An IPv4 address is required.", nameof(ipv4));

            int format = Array.IndexOf(PrefixLengths, prefix.Length);
            if (format < 0 || prefix.PrefixBytes == null || prefix.PrefixBytes.Length != 16)
                throw new ArgumentException("Invalid NAT64 prefix.", nameof(prefix));

            var result = new byte[16];
            Buffer.BlockCopy(prefix.PrefixBytes, 0, result, 0, prefix.Length / 8);
            var v4 = ipv4.GetAddressBytes();
            var octets = Ipv4Octets[format];
            for (int i = 0; i < 4; i++)
                result[octets[i]] = v4[i];
            return new IPAddress(result);
        }

        /// <summary>
        /// Resolves <see cref="WellKnownName"/> through <paramref name="resolve"/> and returns every
        /// distinct Pref64::/n in the order the AAAA answers arrived (RFC 7050 section 3). An empty
        /// list means no DNS64 on this network (A answers only, a negative answer, or a failure).
        /// </summary>
        internal static async Task<List<Nat64Prefix>> DiscoverPrefixesAsync(
            Func<string, Task<IPAddress[]>> resolve,
            CancellationToken ct)
        {
            if (resolve == null) throw new ArgumentNullException(nameof(resolve));

            var prefixes = new List<Nat64Prefix>();
            IPAddress[] answers;
            try
            {
                answers = await resolve(WellKnownName).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // NXDOMAIN, timeout, no resolver: synthesis is impossible (RFC 7050 section 3).
                ct.ThrowIfCancellationRequested();
                return prefixes;
            }

            if (answers == null)
                return prefixes;

            for (int i = 0; i < answers.Length; i++)
            {
                if (TryExtractPrefix(answers[i], out var prefix) && !prefixes.Contains(prefix))
                    prefixes.Add(prefix);
            }

            return prefixes;
        }

        /// <summary>
        /// Connects to <paramref name="resolved"/>. When <paramref name="host"/> is an IPv4 literal and
        /// that connect fails because the host has no IPv4 route, discovers the NAT64 prefixes and
        /// connects to the synthesized IPv6 addresses instead (all prefixes, in discovery order).
        /// Without a prefix the original failure is rethrown unchanged.
        /// </summary>
        internal static async Task<Socket> ConnectWithLiteralFallbackAsync(
            string host,
            IPAddress[] resolved,
            Func<IPAddress[], Task<Socket>> connect,
            Func<string, Task<IPAddress[]>> resolve,
            CancellationToken ct)
        {
            if (connect == null) throw new ArgumentNullException(nameof(connect));

            if (!TryParseIpv4Literal(host, out var literal))
                return await connect(resolved).ConfigureAwait(false);

            try
            {
                return await connect(resolved).ConfigureAwait(false);
            }
            catch (SocketException ex) when (IsNoIpv4RouteError(ex.SocketErrorCode) && !ct.IsCancellationRequested)
            {
                var prefixes = await DiscoverPrefixesAsync(resolve, ct).ConfigureAwait(false);
                if (prefixes.Count == 0)
                    throw;

                var synthesized = new IPAddress[prefixes.Count];
                for (int i = 0; i < prefixes.Count; i++)
                    synthesized[i] = Synthesize(prefixes[i], literal);

                return await connect(synthesized).ConfigureAwait(false);
            }
        }

        /// <summary>True when <paramref name="host"/> is an IPv4 address literal.</summary>
        internal static bool TryParseIpv4Literal(string host, out IPAddress address)
        {
            address = null;
            if (string.IsNullOrEmpty(host) || !IPAddress.TryParse(host, out var parsed))
                return false;
            if (parsed.AddressFamily != AddressFamily.InterNetwork)
                return false;
            address = parsed;
            return true;
        }

        /// <summary>
        /// Connect errors that mean "no IPv4 path from this host" (an IPv6-only network) rather
        /// than "the peer refused or did not answer".
        /// </summary>
        internal static bool IsNoIpv4RouteError(SocketError error)
        {
            switch (error)
            {
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                case SocketError.AddressFamilyNotSupported:
                case SocketError.AddressNotAvailable:
                    return true;
                default:
                    return false;
            }
        }

        private static bool Matches(byte[] address, int[] octets, byte[] ipv4)
        {
            for (int i = 0; i < 4; i++)
            {
                if (address[octets[i]] != ipv4[i])
                    return false;
            }

            return true;
        }
    }
}
