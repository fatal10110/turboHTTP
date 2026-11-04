using System;
using System.Globalization;

namespace TurboHTTP.Core.Internal
{
    /// <summary>
    /// The wire form of a URI host (RFC 3986 Section 3.2.2): an ASCII reg-name (an IDN in its
    /// A-label form, RFC 5890), an IPv4 address, or an IPv6 address in exactly one pair of square
    /// brackets without a zone id. The HTTP transport (<c>HttpAuthority</c>: Host, :authority,
    /// absolute-form, CONNECT, #46/#340) and the WebSocket handshake Host header and proxy CONNECT
    /// authority (#309) all build their host through it, so every stack sends the same bytes and
    /// rejects the same hosts. It lives in Core because the WebSocket core references only Core.
    /// Failures are reported as a message, never an exception type, so each caller keeps its own
    /// error model; no message contains the host.
    /// </summary>
    internal static class WireHost
    {
        internal const string EmptyHostMessage = "The request URI has an empty host and cannot be sent.";

        internal const string NoALabelMessage =
            "The request host has no valid ASCII (A-label) form and cannot be sent.";

        internal const string InvalidHostMessage =
            "The request host is not a valid RFC 3986 host (an IP literal, or labels of 1-63 letters, " +
            "digits, '-' or '_' with at most 253 characters in total) and cannot be sent.";

        private const int MaxDnsNameLength = 253;
        private const int MaxDnsLabelLength = 63;

        /// <summary>
        /// The wire form of <paramref name="uri"/>'s host (no port). Returns <see langword="false"/>
        /// with <paramref name="error"/> set when the host cannot be sent (see <see cref="TryFormat"/>,
        /// and <see cref="Uri.IdnHost"/> throwing for a name IDNA disallows).
        /// </summary>
        internal static bool TryFromUri(Uri uri, out string wireHost, out string error)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            // Uri.Host keeps IPv6 brackets on .NET and drops the zone id; Uri.IdnHost is the
            // A-label form of a DNS name. Either form goes through TryFormat, which accepts the
            // bracketed and the bare IPv6 spelling (Mono/Unity has returned both).
            if (uri.HostNameType == UriHostNameType.IPv6)
                return TryFormat(uri.Host, out wireHost, out error);

            string idnHost;
            try
            {
                idnHost = uri.IdnHost;
            }
            catch (UriFormatException)
            {
                // .NET and Unity Mono throw here for a name IDNA disallows (for example a Unicode
                // noncharacter, #319) instead of returning it Unicode: the same "no A-label form"
                // case TryFormat reports.
                wireHost = null;
                error = NoALabelMessage;
                return false;
            }

            return TryFormat(idnHost, out wireHost, out error);
        }

        /// <summary>
        /// The wire form of a host string as <see cref="Uri.Host"/> or <see cref="Uri.IdnHost"/>
        /// returns it: an IPv6 literal in one pair of brackets without a zone id
        /// (<c>"::1"</c>, <c>"[::1]"</c> and <c>"fe80::1%eth0"</c> become <c>"[::1]"</c> /
        /// <c>"[fe80::1]"</c>), a non-ASCII DNS name in A-label form, anything else unchanged.
        /// Returns <see langword="false"/> with <paramref name="error"/> set when the host cannot be
        /// sent: it is empty (RFC 9110 Sections 4.2.1 and 4.2.2; Unity Mono parses some
        /// IDNA-invalid names as an empty host, #340), a non-ASCII name has no valid A-label form
        /// (for example a label longer than 63 characters, RFC 5890 Section 2.3.2.1), or the ASCII
        /// form is not an RFC 3986 host (see <see cref="IsDnsName"/>).
        /// </summary>
        internal static bool TryFormat(string host, out string wireHost, out string error)
        {
            wireHost = null;
            if (string.IsNullOrEmpty(host))
            {
                error = EmptyHostMessage;
                return false;
            }

            bool bracketed = host.Length > 2 && host[0] == '[' && host[host.Length - 1] == ']';
            var bare = bracketed ? host.Substring(1, host.Length - 2) : host;

            // A reg-name or IPv4 address never contains ':'; an IPv6 literal always does.
            if (bare.IndexOf(':') >= 0)
            {
                // RFC 3986 IP-literal has no zone id; a zone is local to the sending host.
                int zone = bare.IndexOf('%');
                if (zone >= 0)
                    bare = bare.Substring(0, zone);
                if (!IsIPv6Characters(bare))
                {
                    error = InvalidHostMessage;
                    return false;
                }

                wireHost = "[" + bare + "]";
                error = null;
                return true;
            }

            // Brackets hold only an IP literal (RFC 3986 Section 3.2.2); IPvFuture is not supported.
            if (bracketed)
            {
                error = InvalidHostMessage;
                return false;
            }

            string ascii;
            if (IsAscii(bare))
            {
                ascii = bare;
            }
            else
            {
                try
                {
                    // Only reached when the runtime's IdnHost (or a caller-supplied host) is still
                    // Unicode. IdnMapping instances are not documented as thread-safe: one per call.
                    ascii = new IdnMapping().GetAscii(bare);
                }
                catch (ArgumentException)
                {
                    // Header validators accept Latin-1 values, so returning the Unicode name would
                    // put raw non-ASCII bytes on the wire.
                    error = NoALabelMessage;
                    return false;
                }
            }

            // IDNA2003 mapping (Uri.IdnHost, IdnMapping without UseStd3AsciiRules) can produce ASCII
            // that is not a host, for example "xn-- -dha.de" from a no-break space (#340); a field
            // validator accepts SP inside a value, so nothing later would catch it.
            if (!IsDnsName(ascii))
            {
                error = InvalidHostMessage;
                return false;
            }

            wireHost = ascii;
            error = null;
            return true;
        }

        /// <summary>
        /// An ASCII reg-name or IPv4 address as a DNS name (RFC 3986 Section 3.2.2, RFC 1035
        /// Section 2.3.4, RFC 5890 Section 2.3.2.1): labels of 1-63 letters, digits, '-' or '_'
        /// ('_' because resolvers accept it in host names), separated by '.', at most 253
        /// characters, optionally with one trailing '.' (an absolute name). Percent-encoding and
        /// the other RFC 3986 sub-delims are not DNS name characters and are rejected.
        /// </summary>
        private static bool IsDnsName(string value)
        {
            int length = value.Length;
            if (length > 0 && value[length - 1] == '.')
                length--;
            if (length == 0 || length > MaxDnsNameLength)
                return false;

            int labelLength = 0;
            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                if (c == '.')
                {
                    if (labelLength == 0)
                        return false;
                    labelLength = 0;
                    continue;
                }

                bool allowed = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                               || c == '-' || c == '_';
                if (!allowed || ++labelLength > MaxDnsLabelLength)
                    return false;
            }

            return labelLength > 0;
        }

        // Uri has already parsed the address; this only keeps anything that is not an IPv6
        // address character (hex digits, ':' and '.' for an embedded IPv4) off the wire.
        private static bool IsIPv6Characters(string value)
        {
            if (value.Length == 0)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool allowed = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')
                               || c == ':' || c == '.';
                if (!allowed)
                    return false;
            }

            return true;
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
