using System;
using System.Globalization;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Transport.Internal
{
    /// <summary>
    /// The one place that turns a request URI's host into its wire form for the HTTP/1.1
    /// <c>Host</c> header, the HTTP/2 <c>:authority</c> pseudo-header, the absolute-form request
    /// target and the CONNECT authority (#46). RFC 9110 Section 7.2 and RFC 9113 Section 8.3.1:
    /// <c>uri-host [ ":" port ]</c>, where uri-host is an RFC 3986 host: an ASCII reg-name (an IDN
    /// in its A-label form, RFC 5890), an IPv4 address, or an IPv6 address in exactly one pair of
    /// square brackets.
    /// </summary>
    internal static class HttpAuthority
    {
        /// <summary>
        /// <c>host[:port]</c> for <paramref name="uri"/>; the port is omitted when it is the
        /// scheme's default (80 for http, 443 for https). Userinfo is never included.
        /// </summary>
        internal static string FromUri(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            var host = HostFromUri(uri);
            bool isDefaultPort = (uri.Scheme == "https" && uri.Port == 443)
                              || (uri.Scheme == "http" && uri.Port == 80);
            return isDefaultPort
                ? host
                : host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>The wire form of <paramref name="uri"/>'s host (no port), see <see cref="WireHost.TryFromUri"/>.</summary>
        /// <exception cref="HttpFieldValidationException">
        /// The host has no valid ASCII (A-label) form: <see cref="Uri.IdnHost"/> rejected it or
        /// <see cref="FormatHost"/> did.
        /// </exception>
        internal static string HostFromUri(Uri uri)
        {
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            if (!WireHost.TryFromUri(uri, out var host, out var error))
                throw new HttpFieldValidationException(error);
            return host;
        }

        /// <summary>
        /// The wire form of a host string as <see cref="Uri.Host"/> or <see cref="Uri.IdnHost"/>
        /// returns it, see <see cref="WireHost.TryFormat"/> (the implementation moved to Core so the
        /// WebSocket handshake and CONNECT share it, #309): an IPv6 literal in one pair of brackets
        /// without a zone id, a non-ASCII DNS name in A-label form, anything else unchanged.
        /// </summary>
        /// <exception cref="HttpFieldValidationException">
        /// The host cannot be sent: it is empty (RFC 9110 Sections 4.2.1 and 4.2.2: an http(s) URI
        /// with an empty host is invalid), a non-ASCII name has no valid A-label form (for example a
        /// label longer than 63 characters, RFC 5890 Section 2.3.2.1), or the ASCII form is not an
        /// RFC 3986 host (#340). Nothing is sent as raw bytes, and the transport reports it as
        /// <c>InvalidRequest</c>. The message does not contain the host.
        /// </exception>
        internal static string FormatHost(string host)
        {
            if (!WireHost.TryFormat(host, out var wireHost, out var error))
                throw new HttpFieldValidationException(error);
            return wireHost;
        }
    }
}
