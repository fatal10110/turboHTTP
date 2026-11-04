using System;

namespace TurboHTTP.Core
{
    /// <summary>
    /// Capabilities a registered transport reports through
    /// <see cref="HttpTransportFactory.RegisterCapabilities"/>.
    /// </summary>
    [Flags]
    public enum TransportCapabilities
    {
        /// <summary>No capability reported (also the value when no transport is registered).</summary>
        None = 0,

        /// <summary>
        /// The transport can negotiate HTTP/2 (an ALPN-capable TLS provider is available).
        /// </summary>
        Http2 = 1
    }
}
