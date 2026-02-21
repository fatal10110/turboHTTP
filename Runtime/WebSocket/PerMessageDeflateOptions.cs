using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Configuration for permessage-deflate RFC 7692 behavior.
    /// </summary>
    public sealed class PerMessageDeflateOptions
    {
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

            if (clientMaxWindowBits < 8 || clientMaxWindowBits > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(clientMaxWindowBits),
                    clientMaxWindowBits,
                    "ClientMaxWindowBits must be in range [8, 15].");
            }

            if (serverMaxWindowBits < 8 || serverMaxWindowBits > 15)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(serverMaxWindowBits),
                    serverMaxWindowBits,
                    "ServerMaxWindowBits must be in range [8, 15].");
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

        public int CompressionLevel { get; }

        public int ClientMaxWindowBits { get; }

        public int ServerMaxWindowBits { get; }

        public bool ClientNoContextTakeover { get; }

        public bool ServerNoContextTakeover { get; }

        public int CompressionThreshold { get; }

        public static PerMessageDeflateOptions Default => new PerMessageDeflateOptions();
    }
}
