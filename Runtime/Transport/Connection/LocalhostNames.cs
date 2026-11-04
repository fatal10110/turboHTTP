using System;
using System.Net;
using System.Net.Sockets;

namespace TurboHTTP.Transport.Connection
{
    /// <summary>
    /// Localhost names (RFC 6761 section 6.3): <c>localhost</c> and every name under
    /// <c>.localhost</c> always mean the loopback addresses and never reach the platform resolver.
    /// </summary>
    /// <remarks>
    /// The platform resolver cannot be trusted with them: on an Android IL2CPP player the device
    /// hostname is <c>localhost</c>, and the resolver answers it with the interface addresses
    /// (for example 10.0.2.15) and no loopback address, so a connect to <c>localhost</c> never reaches
    /// a server bound to loopback (#322).
    /// </remarks>
    internal static class LocalhostNames
    {
        private const string Label = "localhost";

        /// <summary>
        /// True for <c>localhost</c> and names ending in <c>.localhost</c>, in any letter case,
        /// with or without the trailing root dot.
        /// </summary>
        internal static bool IsLocalhostName(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;

            int length = host.Length;
            if (host[length - 1] == '.')
                length--;

            if (length < Label.Length)
                return false;

            int start = length - Label.Length;
            if (string.Compare(host, start, Label, 0, Label.Length, StringComparison.OrdinalIgnoreCase) != 0)
                return false;

            return start == 0 || (start > 1 && host[start - 1] == '.');
        }

        /// <summary>
        /// The loopback addresses a localhost name resolves to: <c>::1</c> (when the OS supports
        /// IPv6) and <c>127.0.0.1</c>. A new array per call; Happy Eyeballs orders the families.
        /// </summary>
        internal static IPAddress[] GetLoopbackAddresses()
        {
            return Socket.OSSupportsIPv6
                ? new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }
                : new[] { IPAddress.Loopback };
        }
    }
}
