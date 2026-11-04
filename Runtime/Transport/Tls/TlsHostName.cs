using System;
using System.Globalization;

namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Turns the host a caller passes to <see cref="ITlsProvider.WrapAsync"/> (a <see cref="Uri.Host"/>:
    /// IPv6 literals in brackets, IDN names in Unicode) into what goes on the TLS wire. Shared by
    /// both providers so they send the same server name (#142, #46).
    /// </summary>
    internal static class TlsHostName
    {
        /// <summary>
        /// The host without IPv6 brackets: <c>"[::1]"</c> becomes <c>"::1"</c>; anything else is
        /// returned unchanged.
        /// </summary>
        internal static string StripBrackets(string host)
        {
            if (host != null && host.Length > 2 && host[0] == '[' && host[host.Length - 1] == ']')
                return host.Substring(1, host.Length - 2);
            return host;
        }

        /// <summary>True for an IPv4 or IPv6 address literal (with or without brackets).</summary>
        internal static bool IsIpLiteral(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            var bare = StripBrackets(host);
            var kind = Uri.CheckHostName(bare);
            return kind == UriHostNameType.IPv4 || kind == UriHostNameType.IPv6;
        }

        /// <summary>
        /// The <c>server_name</c> to send (RFC 6066 section 3): the ASCII (A-label) form of a DNS
        /// name without a trailing dot, or null for an IP literal (literal addresses are not
        /// permitted in SNI) and for a name that has no valid A-label form.
        /// </summary>
        internal static string ToSniHostName(string host)
        {
            if (string.IsNullOrEmpty(host) || IsIpLiteral(host))
                return null;

            var name = host.Length > 1 && host[host.Length - 1] == '.'
                ? host.Substring(0, host.Length - 1)
                : host;

            if (IsAscii(name))
                return name;

            try
            {
                // IdnMapping instances are not documented as thread-safe; this path only runs
                // for non-ASCII names, once per handshake.
                return new IdnMapping().GetAscii(name);
            }
            catch (ArgumentException)
            {
                // Not a valid IDN: send no SNI. Certificate name matching still uses the caller's
                // host, so a server that needs SNI fails the handshake rather than being trusted.
                return null;
            }
        }

        /// <summary>
        /// The target host for platform TLS APIs (<c>SslStream</c>): the SNI form for a DNS name,
        /// the bare address (no brackets) for an IP literal. Platforms omit SNI for an address and
        /// match it against the certificate's iPAddress entries.
        /// </summary>
        internal static string ToTargetHost(string host)
        {
            if (string.IsNullOrEmpty(host))
                return host;

            if (IsIpLiteral(host))
                return StripBrackets(host);

            return ToSniHostName(host) ?? host;
        }

        private static bool IsAscii(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] > 0x7F)
                    return false;
            }

            return true;
        }
    }
}
