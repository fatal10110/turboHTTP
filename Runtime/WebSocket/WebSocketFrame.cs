using System;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// WebSocket RFC 6455 opcode values.
    /// </summary>
    public enum WebSocketOpcode : byte
    {
        Continuation = 0x0,
        Text = 0x1,
        Binary = 0x2,
        Close = 0x8,
        Ping = 0x9,
        Pong = 0xA
    }

    /// <summary>
    /// RFC 6455 close codes.
    /// </summary>
    public enum WebSocketCloseCode
    {
        NormalClosure = 1000,
        GoingAway = 1001,
        ProtocolError = 1002,
        UnsupportedData = 1003,
        NoStatusReceived = 1005,
        AbnormalClosure = 1006,
        InvalidPayload = 1007,
        PolicyViolation = 1008,
        MessageTooBig = 1009,
        MandatoryExtension = 1010,
        InternalServerError = 1011
    }

    /// <summary>
    /// Immutable frame view representing a single WebSocket wire frame.
    /// </summary>
    public readonly struct WebSocketFrame
    {
        public WebSocketFrame(
            WebSocketOpcode opcode,
            bool isFinal,
            bool isMasked,
            uint maskKey,
            ReadOnlyMemory<byte> payload,
            byte rsvBits = 0)
        {
            if (!WebSocketConstants.TryParseOpcode((byte)opcode, out _))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(opcode),
                    opcode,
                    "Unsupported or reserved opcode.");
            }

            if (IsControlOpcode(opcode))
            {
                if (!isFinal)
                {
                    throw new ArgumentException(
                        "Control frames must not be fragmented (FIN must be set).",
                        nameof(isFinal));
                }

                if (payload.Length > WebSocketConstants.MaxControlFramePayloadLength)
                {
                    throw new ArgumentException(
                        "Control frame payload exceeds 125 bytes.",
                        nameof(payload));
                }
            }

            if (!isMasked && maskKey != 0)
            {
                throw new ArgumentException(
                    "Mask key must be zero when frame is not masked.",
                    nameof(maskKey));
            }

            if ((rsvBits & ~0x70) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(rsvBits),
                    rsvBits,
                    "RSV bits must be a subset of mask 0x70.");
            }

            Opcode = opcode;
            IsFinal = isFinal;
            IsMasked = isMasked;
            MaskKey = isMasked ? maskKey : 0u;
            Payload = payload;
            PayloadLength = payload.Length;
            RsvBits = (byte)(rsvBits & 0x70);
        }

        public WebSocketOpcode Opcode { get; }

        public bool IsFinal { get; }

        public bool IsMasked { get; }

        public uint MaskKey { get; }

        public ReadOnlyMemory<byte> Payload { get; }

        public long PayloadLength { get; }

        public byte RsvBits { get; }

        public bool IsRsv1Set => (RsvBits & 0x40) != 0;

        public bool IsRsv2Set => (RsvBits & 0x20) != 0;

        public bool IsRsv3Set => (RsvBits & 0x10) != 0;

        public bool IsControlFrame => IsControlOpcode(Opcode);

        public bool IsDataFrame => (byte)Opcode <= 0x2;

        internal static bool IsControlOpcode(WebSocketOpcode opcode)
        {
            return (byte)opcode >= 0x8;
        }
    }

    /// <summary>
    /// WebSocket close status payload.
    /// </summary>
    public readonly struct WebSocketCloseStatus
    {
        public WebSocketCloseStatus(WebSocketCloseCode code, string reason = null)
        {
            if (!WebSocketConstants.ValidateCloseCode((int)code, allowReservedLocal: true))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(code),
                    code,
                    "Invalid WebSocket close code.");
            }

            reason = reason ?? string.Empty;

            try
            {
                _ = WebSocketConstants.GetTruncatedCloseReasonByteCount(reason, out var charsToEncode);
                if (charsToEncode < reason.Length)
                    reason = reason.Substring(0, charsToEncode);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Close reason is not valid UTF-8 encodable text.", nameof(reason), ex);
            }

            Code = code;
            Reason = reason;
        }

        public WebSocketCloseCode Code { get; }

        public string Reason { get; }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(Reason))
                return ((int)Code).ToString();

            return ((int)Code) + " " + Reason;
        }
    }
}
