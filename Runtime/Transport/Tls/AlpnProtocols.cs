namespace TurboHTTP.Transport.Tls
{
    /// <summary>
    /// Shared ALPN offer lists (RFC 7301 section 3.1), allocated once instead of per handshake
    /// (#175). The arrays are shared by every connection: callers and <see cref="ITlsProvider"/>
    /// implementations only read them and must never write to them.
    /// </summary>
    internal static class AlpnProtocols
    {
        /// <summary>
        /// "h2" first, then "http/1.1": HTTP/2 is selected only through ALPN (RFC 9113 section 3.2)
        /// and HTTP/1.1 stays available when the server or runtime does not negotiate it.
        /// </summary>
        internal static readonly string[] H2AndHttp11 = { "h2", "http/1.1" };
    }
}
