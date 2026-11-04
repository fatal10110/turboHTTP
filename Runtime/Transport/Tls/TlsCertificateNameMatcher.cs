using System;
using System.Collections.Generic;
using System.Net;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Server identity check for a TLS certificate (RFC 6125 section 6), used by providers that do
    /// not get it from the platform (#33). Input is what the certificate presents, already parsed:
    /// the subjectAltName dNSName and iPAddress entries and the subject CN.
    /// </summary>
    /// <remarks>
    /// Rules, each the conservative reading of the RFC:
    /// <list type="bullet">
    /// <item>The target is <see cref="TlsHostName.ToTargetHost"/>: A-labels for an IDN (RFC 6125
    /// 6.4.2, RFC 5280 7.2), the bare address for an IP literal, no trailing dot.</item>
    /// <item>An IP literal matches only an iPAddress entry of the same family and octets (RFC 6125
    /// 1.7.2, RFC 5280 4.2.1.6); never a dNSName or the CN.</item>
    /// <item>A DNS name matches a dNSName entry case-insensitively; the CN is consulted only when the
    /// certificate has no subjectAltName extension at all (RFC 6125 6.4.4).</item>
    /// <item>A wildcard is honoured only as the complete left-most label (<c>*.example.com</c>),
    /// matches exactly one non-empty label, and needs at least two labels after it, so
    /// <c>*.com</c>, <c>f*.example.com</c> and <c>www.*.com</c> match nothing (RFC 6125 6.4.3).</item>
    /// <item>Non-ASCII presented names never match (certificates carry A-labels).</item>
    /// </list>
    /// </remarks>
    internal static class TlsCertificateNameMatcher
    {
        /// <summary>
        /// True when the certificate identity matches <paramref name="host"/> (the caller's host,
        /// as passed to <see cref="ITlsProvider.WrapAsync"/>).
        /// </summary>
        /// <param name="host">Host the caller connected to (Uri.Host form).</param>
        /// <param name="hasSubjectAltName">True when the certificate has a subjectAltName extension.</param>
        /// <param name="dnsNames">The subjectAltName dNSName entries.</param>
        /// <param name="ipAddresses">The subjectAltName iPAddress entries (raw octets).</param>
        /// <param name="commonNames">The subject CN values.</param>
        internal static bool Matches(
            string host,
            bool hasSubjectAltName,
            IReadOnlyList<string> dnsNames,
            IReadOnlyList<byte[]> ipAddresses,
            IReadOnlyList<string> commonNames)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            if (TlsHostName.IsIpLiteral(host))
                return MatchesIp(TlsHostName.StripBrackets(host), ipAddresses);

            var target = NormalizeDnsHost(TlsHostName.ToTargetHost(host));
            if (target == null)
                return false;

            if (dnsNames != null)
            {
                for (int i = 0; i < dnsNames.Count; i++)
                {
                    if (MatchesDnsPattern(dnsNames[i], target))
                        return true;
                }
            }

            if (hasSubjectAltName || commonNames == null)
                return false;

            // RFC 6125 6.4.4: CN-ID only without any subjectAltName. A subject with several CNs is
            // ambiguous; accept none of them rather than pick one.
            return commonNames.Count == 1 && MatchesDnsPattern(commonNames[0], target);
        }

        private static bool MatchesIp(string address, IReadOnlyList<byte[]> ipAddresses)
        {
            if (ipAddresses == null || !IPAddress.TryParse(address, out var parsed))
                return false;

            // GetAddressBytes drops an IPv6 scope id; an IPv4 host never matches an IPv4-mapped
            // IPv6 entry (different length), and vice versa.
            var expected = parsed.GetAddressBytes();
            for (int i = 0; i < ipAddresses.Count; i++)
            {
                var candidate = ipAddresses[i];
                if (candidate != null && candidate.Length == expected.Length && SameBytes(candidate, expected))
                    return true;
            }
            return false;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The reference identifier (<see cref="TlsHostName.ToTargetHost"/> already removed one
        /// trailing dot): ASCII, no empty label. Null when the host cannot match any certificate name.
        /// </summary>
        private static string NormalizeDnsHost(string host)
        {
            if (string.IsNullOrEmpty(host) || !IsValidAsciiName(host, allowWildcard: false))
                return null;

            return host;
        }

        private static bool MatchesDnsPattern(string pattern, string target)
        {
            if (string.IsNullOrEmpty(pattern) || !IsValidAsciiName(pattern, allowWildcard: true))
                return false;

            if (pattern.IndexOf('*') < 0)
                return string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase);

            // Only "*." + at least two complete labels, and no other '*'.
            if (pattern.Length < 2 || pattern[0] != '*' || pattern[1] != '.')
                return false;

            var suffix = pattern.Substring(2);
            if (suffix.IndexOf('*') >= 0 || suffix.IndexOf('.') < 0)
                return false;

            int firstDot = target.IndexOf('.');
            if (firstDot <= 0)
                return false;

            return string.Equals(target.Substring(firstDot + 1), suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Printable ASCII without spaces, no empty labels, and '*' only when allowed (the pattern
        /// shape is checked by the caller).
        /// </summary>
        private static bool IsValidAsciiName(string name, bool allowWildcard)
        {
            if (name[0] == '.' || name[name.Length - 1] == '.')
                return false;

            char previous = '\0';
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c <= ' ' || c >= 0x7F)
                    return false;
                if (c == '*' && !allowWildcard)
                    return false;
                if (c == '.' && previous == '.')
                    return false;
                previous = c;
            }
            return true;
        }
    }
}
