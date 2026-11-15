using System;
using System.Security.Cryptography;
using System.Text;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// WebSocket protocol constants and utility helpers.
    /// </summary>
    internal static class WebSocketConstants
    {
        /// <summary>
        /// GUID appended to Sec-WebSocket-Key to derive Sec-WebSocket-Accept (RFC 6455 Section 1.3, 4.2.2).
        /// </summary>
        public const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        public const int SupportedVersion = 13;

        public const int DefaultMaxMessageSize = 4 * 1024 * 1024;

        /// <summary>
        /// Default largest received frame; equal to <see cref="DefaultMaxMessageSize"/> because a frame
        /// cannot exceed a message.
        /// </summary>
        public const int DefaultMaxFrameSize = DefaultMaxMessageSize;

        /// <summary>
        /// Default largest number of frames one message may arrive in (#1144). RFC 6455 section 5.4 puts no
        /// bound on fragments; the bytes are already bounded by <see cref="DefaultMaxMessageSize"/>, so this
        /// only stops a message that never ends in empty or tiny fragments. 4096 admits a default-size
        /// (4 MiB) message in 1 KiB fragments.
        /// </summary>
        public const int DefaultMaxFragmentCount = 4096;
        public const int DefaultFragmentationThreshold = 64 * 1024;
        public const int DefaultReceiveQueueCapacity = 100;

        /// <summary>Default byte budget of each receive delivery queue: two default-size messages.</summary>
        public const int DefaultReceiveQueueMaxBytes = 2 * DefaultMaxMessageSize;

        public static readonly TimeSpan DefaultCloseHandshakeTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan DefaultPingInterval = TimeSpan.FromSeconds(25);
        public static readonly TimeSpan DefaultPongTimeout = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Upper bound of the grace period the keep-alive waits after a pong timeout, re-checking for a Pong,
        /// inbound bytes or an application resume before declaring the peer dead (#1132). The grace period is the
        /// smaller of this and <see cref="WebSocketConnectionOptions.PongTimeout"/>.
        /// </summary>
        internal static readonly TimeSpan KeepAliveTimeoutGracePeriod = TimeSpan.FromSeconds(1);

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
        /// <summary>The upgrade was rejected or its response failed validation (RFC 6455 section 4.1).</summary>
        HandshakeFailed,
        /// <summary><c>Sec-WebSocket-Extensions</c> negotiation failed, or a required extension was not negotiated.</summary>
        ExtensionNegotiationFailed,
        /// <summary>The connection closed, or the operation needs an open connection.</summary>
        ConnectionClosed,
        /// <summary>Writing to the connection failed.</summary>
        SendFailed,
        /// <summary>Reading from the connection failed.</summary>
        ReceiveFailed,
        /// <summary>A message exceeded the configured message size or fragment count limit.</summary>
        MessageTooLarge,
        /// <summary>No pong arrived within <see cref="WebSocketConnectionOptions.PongTimeout"/> after a keep-alive ping.</summary>
        PongTimeout,
        /// <summary>The peer sent a malformed frame.</summary>
        InvalidFrame,
        /// <summary>A frame exceeded <see cref="WebSocketConnectionOptions.MaxFrameSize"/>.</summary>
        FrameTooLarge,
        /// <summary>A close frame carried an invalid status code or payload (RFC 6455 section 7.4).</summary>
        InvalidCloseCode,
        /// <summary>A text message or close reason was not valid UTF-8.</summary>
        InvalidUtf8,
        /// <summary>The server sent a masked frame (RFC 6455 section 5.1).</summary>
        MaskedServerFrame,
        /// <summary>A continuation frame arrived without a message in progress, or a data frame interrupted a fragmented message.</summary>
        UnexpectedContinuation,
        /// <summary>A frame used a reserved opcode.</summary>
        ReservedOpcode,
        /// <summary>The peer or an extension violated RFC 6455 in another way.</summary>
        ProtocolViolation,
        /// <summary>A frame declared a payload length that is invalid or too large to represent.</summary>
        PayloadLengthOverflow,
        /// <summary>A typed serializer failed to encode or decode a message.</summary>
        SerializationFailed,
        /// <summary>The proxy answered the CONNECT request with 407.</summary>
        ProxyAuthenticationRequired,
        /// <summary>Connecting to the proxy failed.</summary>
        ProxyConnectionFailed,
        /// <summary>The proxy refused or broke the CONNECT tunnel.</summary>
        ProxyTunnelFailed,
        /// <summary>permessage-deflate failed to compress an outbound message.</summary>
        CompressionFailed,
        /// <summary>permessage-deflate failed to decompress an inbound message.</summary>
        DecompressionFailed,
        /// <summary>A decompressed message exceeded <see cref="WebSocketConnectionOptions.MaxMessageSize"/>.</summary>
        DecompressedMessageTooLarge,

        /// <summary>
        /// The connection would carry cleartext (a <c>ws://</c> URI or an <c>http://</c> proxy
        /// leg) but <see cref="WebSocketConnectionOptions.AllowCleartext"/> is
        /// <see langword="false"/> or the platform cleartext policy refuses the host. Raised before
        /// any socket is opened.
        /// </summary>
        CleartextNotPermitted,

        /// <summary>
        /// The connect did not finish in time: <see cref="WebSocketConnectionOptions.HandshakeTimeout"/>
        /// expired (TCP connect, CONNECT tunnel, TLS and the upgrade exchange) or the transport's connect
        /// timeout did. Reported as <see cref="TurboHTTP.Core.UHttpErrorType.Timeout"/>; retryable.
        /// </summary>
        Timeout,

        /// <summary>
        /// TLS authentication of the server failed: its certificate was rejected (RFC 5280 path or
        /// RFC 6125 name validation) or the handshake could not be authenticated. Reported as
        /// <see cref="TurboHTTP.Core.UHttpErrorType.CertificateError"/>; not retryable, and the
        /// resilient client does not reconnect into it.
        /// </summary>
        CertificateError,

        /// <summary>
        /// The host name of the server (or of the proxy) could not be resolved: no such name, no
        /// addresses, a resolver failure or the DNS timeout. Reported as
        /// <see cref="TurboHTTP.Core.UHttpErrorType.NetworkError"/>; retryable.
        /// </summary>
        DnsResolutionFailed,

        /// <summary>
        /// The connect request cannot be carried out as given: for example a URI scheme other than
        /// <c>ws</c>/<c>wss</c>, or an option value the transport rejects. Reported as
        /// <see cref="TurboHTTP.Core.UHttpErrorType.InvalidRequest"/>; not retryable, and the resilient
        /// client does not reconnect into it.
        /// </summary>
        InvalidRequest
    }

    /// <summary>
    /// Exception used for frame-level protocol violations.
    /// </summary>
    internal sealed class WebSocketProtocolException : Exception
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
