using System;
using System.Security.Cryptography;
using System.Text;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// WebSocket protocol constants and utility helpers.
    /// </summary>
    public static class WebSocketConstants
    {
        public const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-5AB53DC52D51";
        public const int SupportedVersion = 13;

        public const int DefaultMaxFrameSize = 16 * 1024 * 1024;
        public const int DefaultMaxMessageSize = 4 * 1024 * 1024;
        public const int DefaultMaxFragmentCount = 64;
        public const int DefaultFragmentationThreshold = 64 * 1024;
        public const int DefaultReceiveQueueCapacity = 100;

        public static readonly TimeSpan DefaultCloseHandshakeTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultPingInterval = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan DefaultPongTimeout = TimeSpan.FromSeconds(10);

        public const int MaxControlFramePayloadLength = 125;
        public const int MaxCloseReasonUtf8Bytes = 123;

        // Throw on invalid surrogate input so malformed text cannot be silently replaced.
        internal static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static volatile bool _sha1Checked;
        private static readonly object SharedRngGate = new object();
        private static readonly RandomNumberGenerator SharedRng = CreateSharedRng();

        /// <summary>
        /// Computes Sec-WebSocket-Accept for handshake validation (RFC 6455 Section 4.2.2).
        /// </summary>
        public static string ComputeAcceptKey(string clientKey)
        {
            if (string.IsNullOrWhiteSpace(clientKey))
                throw new ArgumentNullException(nameof(clientKey));

            EnsureSha1Available();

            using (var sha1 = SHA1.Create())
            {
                if (sha1 == null)
                    throw new PlatformNotSupportedException("SHA-1 provider is unavailable on this platform.");

                var acceptSource = clientKey + WebSocketGuid;
                var sourceBytes = Encoding.ASCII.GetBytes(acceptSource);
                var hash = sha1.ComputeHash(sourceBytes);
                return Convert.ToBase64String(hash);
            }
        }

        /// <summary>
        /// Generates a base64-encoded 16-byte Sec-WebSocket-Key (RFC 6455 Section 4.1).
        /// </summary>
        public static string GenerateClientKey()
        {
            var keyBytes = new byte[16];
            lock (SharedRngGate)
            {
                SharedRng.GetBytes(keyBytes);
            }

            return Convert.ToBase64String(keyBytes);
        }

        /// <summary>
        /// Validates a close code against RFC 6455 Section 7.4 ranges.
        /// </summary>
        /// <param name="code">Close code to validate.</param>
        /// <param name="allowReservedLocal">
        /// When true, allows 1005/1006 for local signaling only. When false, rejects
        /// them for wire transmission.
        /// </param>
        public static bool ValidateCloseCode(int code, bool allowReservedLocal = false)
        {
            if (code < 1000 || code >= 5000)
                return false;

            if (code == 1005 || code == 1006)
                return allowReservedLocal;

            // Reserved by RFC 6455 and must not be sent by endpoints.
            if (code == 1004 || code == 1015)
                return false;

            // Reserved for extensions and frameworks, not endpoint apps.
            if (code >= 1016 && code <= 2999)
                return false;

            return true;
        }

        /// <summary>
        /// Computes the UTF-8 byte count for a close reason and truncates at codepoint boundaries
        /// to RFC 6455's 123-byte close reason limit.
        /// </summary>
        internal static int GetTruncatedCloseReasonByteCount(string reason, out int charsToEncode)
        {
            reason = reason ?? string.Empty;
            charsToEncode = 0;

            if (reason.Length == 0)
                return 0;

            int fullByteCount = StrictUtf8.GetByteCount(reason);
            if (fullByteCount <= MaxCloseReasonUtf8Bytes)
            {
                charsToEncode = reason.Length;
                return fullByteCount;
            }

            int usedBytes = 0;
            int index = 0;

            while (index < reason.Length)
            {
                int charCount = 1;
                char current = reason[index];

                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= reason.Length || !char.IsLowSurrogate(reason[index + 1]))
                        throw new ArgumentException("Close reason contains an invalid surrogate pair.", nameof(reason));

                    charCount = 2;
                }
                else if (char.IsLowSurrogate(current))
                {
                    throw new ArgumentException("Close reason contains an invalid surrogate pair.", nameof(reason));
                }

                int charBytes = StrictUtf8.GetByteCount(reason, index, charCount);
                if (usedBytes + charBytes > MaxCloseReasonUtf8Bytes)
                    break;

                usedBytes += charBytes;
                index += charCount;
            }

            charsToEncode = index;
            return usedBytes;
        }

        /// <summary>
        /// Returns true when the opcode falls into reserved RFC ranges.
        /// </summary>
        public static bool IsReservedOpcode(byte opcode)
        {
            return (opcode >= 0x3 && opcode <= 0x7) || (opcode >= 0xB && opcode <= 0xF);
        }

        internal static bool TryParseOpcode(byte value, out WebSocketOpcode opcode)
        {
            switch (value)
            {
                case (byte)WebSocketOpcode.Continuation:
                case (byte)WebSocketOpcode.Text:
                case (byte)WebSocketOpcode.Binary:
                case (byte)WebSocketOpcode.Close:
                case (byte)WebSocketOpcode.Ping:
                case (byte)WebSocketOpcode.Pong:
                    opcode = (WebSocketOpcode)value;
                    return true;
                default:
                    opcode = default(WebSocketOpcode);
                    return false;
            }
        }

        /// <summary>
        /// Verifies SHA-1 availability at runtime and throws a descriptive exception when missing.
        /// </summary>
        public static void EnsureSha1Available()
        {
            if (_sha1Checked)
                return;

            try
            {
                using (var sha1 = SHA1.Create())
                {
                    if (sha1 == null)
                    {
                        throw new PlatformNotSupportedException(
                            "SHA-1 is unavailable. WebSocket handshake requires System.Security.Cryptography.SHA1.");
                    }

                    _ = sha1.ComputeHash(Array.Empty<byte>());
                }

                _sha1Checked = true;
            }
            catch (PlatformNotSupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PlatformNotSupportedException(
                    "SHA-1 is unavailable. Ensure IL2CPP linker preserves SHA1 and SHA1Managed.",
                    ex);
            }
        }

        private static RandomNumberGenerator CreateSharedRng()
        {
            var rng = RandomNumberGenerator.Create();
            if (rng == null)
            {
                throw new PlatformNotSupportedException(
                    "Random number generator is unavailable on this platform.");
            }

            return rng;
        }
    }

    /// <summary>
    /// WebSocket protocol-level error categories.
    /// </summary>
    public enum WebSocketError
    {
        HandshakeFailed,
        ExtensionNegotiationFailed,
        ConnectionClosed,
        SendFailed,
        ReceiveFailed,
        MessageTooLarge,
        PongTimeout,
        InvalidFrame,
        FrameTooLarge,
        InvalidCloseCode,
        InvalidUtf8,
        MaskedServerFrame,
        UnexpectedContinuation,
        ReservedOpcode,
        ProtocolViolation,
        PayloadLengthOverflow,
        SerializationFailed,
        ProxyAuthenticationRequired,
        ProxyConnectionFailed,
        ProxyTunnelFailed,
        CompressionFailed,
        DecompressionFailed,
        DecompressedMessageTooLarge
    }

    /// <summary>
    /// Exception used for frame-level protocol violations.
    /// </summary>
    public sealed class WebSocketProtocolException : Exception
    {
        public WebSocketError Error { get; }

        public WebSocketCloseCode CloseCode { get; }

        public WebSocketProtocolException(
            WebSocketError error,
            string message,
            WebSocketCloseCode closeCode = WebSocketCloseCode.ProtocolError,
            Exception innerException = null)
            : base(message, innerException)
        {
            Error = error;
            CloseCode = closeCode;
        }
    }
}
