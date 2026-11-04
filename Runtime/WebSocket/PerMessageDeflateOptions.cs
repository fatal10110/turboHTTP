using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Configuration for permessage-deflate RFC 7692 behavior.
    /// </summary>
    public sealed class PerMessageDeflateOptions
    {
        /// <summary>
        /// Creates permessage-deflate options.
        /// </summary>
        /// <param name="compressionLevel">Outbound compression level, 0 (none) to 9 (best).</param>
        /// <param name="clientMaxWindowBits">
        /// Must be 15. Outbound compression always uses a 15-bit window, so a smaller client window cannot be
        /// honoured and <c>client_max_window_bits</c> is never offered (RFC 7692 section 7.1.2.2).
        /// </param>
        /// <param name="serverMaxWindowBits">Largest server window offered, 8 to 15 (RFC 7692 section 7.1.2.1).</param>
        /// <param name="clientNoContextTakeover">Offer <c>client_no_context_takeover</c>.</param>
        /// <param name="serverNoContextTakeover">
        /// Must be true. Inbound messages are inflated without context retention, so
        /// <c>server_no_context_takeover</c> is always offered and required in the response
        /// (RFC 7692 section 7.1.1.1).
        /// </param>
        /// <param name="compressionThreshold">Outbound messages smaller than this many bytes are sent uncompressed.</param>
        /// <exception cref="ArgumentOutOfRangeException">A value is out of range or not supported.</exception>
        public PerMessageDeflateOptions(
            int compressionLevel = 6,
            int clientMaxWindowBits = 15,
            int serverMaxWindowBits = 15,
            bool clientNoContextTakeover = true,
            bool serverNoContextTakeover = true,
            int compressionThreshold = 128)
        {
            if (compressionLevel < 0 || compressionLevel > 9)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(compressionLevel),
                    compressionLevel,
                    "CompressionLevel must be in range [0, 9].");
            }

            if (clientMaxWindowBits != 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(clientMaxWindowBits),
                    clientMaxWindowBits,
                    "ClientMaxWindowBits must be 15: outbound compression always uses a 15-bit window.");
            }

            if (serverMaxWindowBits < 8 || serverMaxWindowBits > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(serverMaxWindowBits),
                    serverMaxWindowBits,
                    "ServerMaxWindowBits must be in range [8, 15].");
            }

            if (!serverNoContextTakeover)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(serverNoContextTakeover),
                    serverNoContextTakeover,
                    "ServerNoContextTakeover must be true: inbound context takeover is not supported.");
            }

            if (compressionThreshold < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(compressionThreshold),
                    compressionThreshold,
                    "CompressionThreshold must be >= 0.");
            }

            CompressionLevel = compressionLevel;
            ClientMaxWindowBits = clientMaxWindowBits;
            ServerMaxWindowBits = serverMaxWindowBits;
            ClientNoContextTakeover = clientNoContextTakeover;
            ServerNoContextTakeover = serverNoContextTakeover;
            CompressionThreshold = compressionThreshold;
        }

        /// <summary>Outbound compression level, 0 to 9.</summary>
        public int CompressionLevel { get; }

        /// <summary>Always 15: the outbound window size; never offered to the server.</summary>
        public int ClientMaxWindowBits { get; }

        /// <summary>Largest server window offered as <c>server_max_window_bits</c>.</summary>
        public int ServerMaxWindowBits { get; }

        /// <summary>Whether <c>client_no_context_takeover</c> is offered.</summary>
        public bool ClientNoContextTakeover { get; }

        /// <summary>Always true: <c>server_no_context_takeover</c> is always offered and required.</summary>
        public bool ServerNoContextTakeover { get; }

        /// <summary>Outbound messages smaller than this many bytes are sent uncompressed.</summary>
        public int CompressionThreshold { get; }

        /// <summary>A new instance with the default values.</summary>
        public static PerMessageDeflateOptions Default => new PerMessageDeflateOptions();
    }
}
