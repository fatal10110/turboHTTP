using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// The IP-literal identity check for the platform <c>SslStream</c> provider (#113). An IP host
    /// matches only an iPAddress subjectAltName entry (RFC 6125 1.7.2, RFC 5280 4.2.1.6), never a
    /// dNSName or CN that spells the address. Some platforms do accept such a dNSName (.NET's own
    /// host-name check compares the address text with dNSName entries too), so the provider applies
    /// this check after the platform's check has passed. It can only reject more, never accept.
    /// </summary>
    internal static class SubjectAltNameIpAddresses
    {
        private const string SubjectAltNameOid = "2.5.29.17";

        /// <summary>
        /// Throws <see cref="AuthenticationException"/> when <paramref name="host"/> is an IP literal
        /// and <paramref name="certificate"/> has no iPAddress subjectAltName entry for it. A DNS host
        /// is left to the platform's check.
        /// </summary>
        internal static void EnsureIpLiteralIdentity(string host, X509Certificate certificate)
        {
            if (!TlsHostName.IsIpLiteral(host))
                return;

            byte[] extension = null;
            if (certificate != null)
            {
                var owned = certificate as X509Certificate2;
                var cert = owned ?? new X509Certificate2(certificate);
                try
                {
                    extension = cert.Extensions[SubjectAltNameOid]?.RawData;
                }
                finally
                {
                    if (owned == null)
                        cert.Dispose();
                }
            }

            if (!TlsCertificateNameMatcher.Matches(
                    host, extension != null, Array.Empty<string>(), Read(extension), Array.Empty<string>()))
            {
                throw new AuthenticationException(
                    "The server certificate has no iPAddress subjectAltName for " + TlsHostName.StripBrackets(host) +
                    "; a dNSName or CN that spells an address does not identify it (RFC 6125 1.7.2).");
            }
        }

        /// <summary>
        /// The iPAddress entries (raw octets) of a DER subjectAltName extension value
        /// (<c>GeneralNames ::= SEQUENCE OF GeneralName</c>, iPAddress is <c>[7] IMPLICIT OCTET
        /// STRING</c>). Malformed or missing input yields no entries, so the check fails closed.
        /// </summary>
        internal static IReadOnlyList<byte[]> Read(byte[] der)
        {
            var result = new List<byte[]>();
            if (der == null)
                return result;

            int offset = 0;
            if (!TryReadHeader(der, ref offset, der.Length, out byte tag, out int length) || tag != 0x30
                || offset + length != der.Length)
                return result;

            int end = offset + length;
            while (offset < end)
            {
                if (!TryReadHeader(der, ref offset, end, out tag, out length))
                    return new List<byte[]>();

                // Context-specific primitive [7]: iPAddress. Only 4 (IPv4) or 16 (IPv6) octets are
                // addresses; anything else (e.g. a name-constraint style address/mask) is not.
                if (tag == 0x87 && (length == 4 || length == 16))
                {
                    var address = new byte[length];
                    Buffer.BlockCopy(der, offset, address, 0, length);
                    result.Add(address);
                }

                offset += length;
            }

            return result;
        }

        private static bool TryReadHeader(byte[] der, ref int offset, int end, out byte tag, out int length)
        {
            tag = 0;
            length = 0;
            if (offset + 2 > end)
                return false;

            tag = der[offset++];
            // Multi-byte tags (low five bits all set) never occur in GeneralNames.
            if ((tag & 0x1F) == 0x1F)
                return false;

            int first = der[offset++];
            if (first < 0x80)
            {
                length = first;
            }
            else
            {
                int count = first & 0x7F;
                if (count == 0 || count > 3 || offset + count > end)
                    return false;
                for (int i = 0; i < count; i++)
                    length = (length << 8) | der[offset++];
            }

            return length <= end - offset;
        }
    }
}
