using System;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HTTP/2 frame types. RFC 7540 Section 6.
    /// </summary>
    internal enum Http2FrameType : byte
    {
        Data         = 0x0,
        Headers      = 0x1,
        Priority     = 0x2,
        RstStream    = 0x3,
        Settings     = 0x4,
        PushPromise  = 0x5,
        Ping         = 0x6,
        GoAway       = 0x7,
        WindowUpdate = 0x8,
        Continuation = 0x9
    }

    /// <summary>
    /// HTTP/2 frame flags. RFC 7540 Section 6.
    /// EndStream and Ack share the same bit value (0x1) — they apply to different frame types.
    /// </summary>
    [Flags]
    internal enum Http2FrameFlags : byte
    {
        None        = 0x0,
        EndStream   = 0x1,
        Ack         = 0x1,
        EndHeaders  = 0x4,
        Padded      = 0x8,
        HasPriority = 0x20
    }

    /// <summary>
    /// HTTP/2 error codes. RFC 7540 Section 7.
    /// </summary>
    internal enum Http2ErrorCode : uint
    {
        NoError            = 0x0,
        ProtocolError      = 0x1,
        InternalError      = 0x2,
        FlowControlError   = 0x3,
        SettingsTimeout    = 0x4,
        StreamClosed       = 0x5,
        FrameSizeError     = 0x6,
        RefusedStream      = 0x7,
        Cancel             = 0x8,
        CompressionError   = 0x9,
        ConnectError       = 0xa,
        EnhanceYourCalm    = 0xb,
        InadequateSecurity = 0xc,
        Http11Required     = 0xd
    }

    /// <summary>
    /// HTTP/2 settings identifiers. RFC 7540 Section 6.5.2.
    /// </summary>
    internal enum Http2SettingId : ushort
    {
        HeaderTableSize      = 0x1,
        EnablePush           = 0x2,
        MaxConcurrentStreams  = 0x3,
        InitialWindowSize    = 0x4,
        MaxFrameSize         = 0x5,
        MaxHeaderListSize    = 0x6
    }

    /// <summary>
    /// Represents a single HTTP/2 frame (9-byte header + payload). RFC 7540 Section 4.1.
    /// </summary>
    internal class Http2Frame
    {
        /// <summary>Payload length (24-bit, max configurable via SETTINGS_MAX_FRAME_SIZE).</summary>
        public int Length { get; set; }

        /// <summary>Frame type byte.</summary>
        public Http2FrameType Type { get; set; }

        /// <summary>Frame flags byte.</summary>
        public Http2FrameFlags Flags { get; set; }

        /// <summary>Stream identifier (31-bit, high bit reserved and always masked to 0).</summary>
        public int StreamId { get; set; }

        /// <summary>Frame payload bytes. Empty array (not null) for zero-payload frames.</summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) != 0;
    }

    /// <summary>
    /// HTTP/2 protocol constants. RFC 7540 Sections 4, 6.5.2.
    /// </summary>
    internal static class Http2Constants
    {
        public const int FrameHeaderSize = 9;
        public const int DefaultMaxFrameSize = 16384;        // 2^14
        public const int MaxFrameSizeMin = 16384;             // SETTINGS_MAX_FRAME_SIZE minimum
        public const int MaxFrameSizeMax = 16777215;          // 2^24 - 1
        public const int DefaultInitialWindowSize = 65535;    // 2^16 - 1
        public const int MaxWindowSize = 2147483647;          // 2^31 - 1
        public const int DefaultHeaderTableSize = 4096;
        public const int SettingEntrySize = 6;                // 2 bytes ID + 4 bytes value

        /// <summary>
        /// The HTTP/2 connection preface: "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"
        /// Must be sent by the client before any frames. RFC 7540 Section 3.5.
        /// </summary>
        public static readonly byte[] ConnectionPreface = System.Text.Encoding.ASCII.GetBytes(
            "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
    }

    /// <summary>
    /// Exception thrown for HTTP/2 protocol errors that require connection-level error handling.
    /// </summary>
    internal class Http2ProtocolException : Exception
    {
        public Http2ErrorCode ErrorCode { get; }

        public Http2ProtocolException(Http2ErrorCode errorCode, string message)
            : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Exception thrown for HPACK decoding errors (COMPRESSION_ERROR).
    /// </summary>
    internal class HpackDecodingException : Exception
    {
        public HpackDecodingException(string message) : base(message) { }
    }
}
