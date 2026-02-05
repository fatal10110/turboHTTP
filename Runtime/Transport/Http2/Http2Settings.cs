using System.Collections.Generic;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// HTTP/2 connection settings. RFC 7540 Section 6.5, 6.5.2.
    /// Tracks server-sent settings. Provides serialization for client settings.
    /// </summary>
    internal class Http2Settings
    {
        public int HeaderTableSize { get; private set; } = Http2Constants.DefaultHeaderTableSize;
        public bool EnablePush { get; private set; } = true;
        public int MaxConcurrentStreams { get; private set; } = int.MaxValue;
        public int InitialWindowSize { get; private set; } = Http2Constants.DefaultInitialWindowSize;
        public int MaxFrameSize { get; private set; } = Http2Constants.DefaultMaxFrameSize;
        public int MaxHeaderListSize { get; private set; } = int.MaxValue;

        /// <summary>
        /// Maximum response body size in bytes. Prevents unbounded MemoryStream growth
        /// from malicious or misconfigured servers. Default 100 MB. Set to 0 for unlimited.
        /// This is a client-side limit, not part of the HTTP/2 protocol.
        /// </summary>
        public long MaxResponseBodySize { get; set; } = 100 * 1024 * 1024; // 100 MB default

        /// <summary>
        /// Apply a single setting received from the server.
        /// Validates per RFC 7540 Section 6.5.2.
        /// </summary>
        public void Apply(Http2SettingId id, uint value)
        {
            switch (id)
            {
                case Http2SettingId.HeaderTableSize:
                    HeaderTableSize = ClampToIntMax(value);
                    break;

                case Http2SettingId.EnablePush:
                    if (value > 1)
                        throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                            "ENABLE_PUSH must be 0 or 1");
                    EnablePush = value == 1;
                    break;

                case Http2SettingId.MaxConcurrentStreams:
                    MaxConcurrentStreams = ClampToIntMax(value);
                    break;

                case Http2SettingId.InitialWindowSize:
                    if (value > 2147483647)
                        throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                            "INITIAL_WINDOW_SIZE exceeds maximum");
                    InitialWindowSize = (int)value;
                    break;

                case Http2SettingId.MaxFrameSize:
                    if (value < 16384 || value > 16777215)
                        throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                            "MAX_FRAME_SIZE out of range");
                    MaxFrameSize = (int)value;
                    break;

                case Http2SettingId.MaxHeaderListSize:
                    MaxHeaderListSize = ClampToIntMax(value);
                    break;

                default:
                    // Unknown settings MUST be ignored (RFC 7540 Section 6.5.2)
                    break;
            }
        }

        /// <summary>
        /// Clamp 32-bit unsigned SETTINGS values to int.MaxValue to avoid overflow.
        /// HTTP/2 SETTINGS use uint32; values above int.MaxValue are treated as "very large".
        /// </summary>
        private static int ClampToIntMax(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        /// <summary>
        /// Serialize client settings for the initial SETTINGS frame.
        /// Sends ENABLE_PUSH=0 and MAX_CONCURRENT_STREAMS=100.
        /// </summary>
        public byte[] SerializeClientSettings()
        {
            var settings = new List<(Http2SettingId, uint)>
            {
                (Http2SettingId.EnablePush, 0),
                (Http2SettingId.MaxConcurrentStreams, 100),
            };

            byte[] payload = new byte[settings.Count * Http2Constants.SettingEntrySize];
            for (int i = 0; i < settings.Count; i++)
            {
                var (id, value) = settings[i];
                int offset = i * Http2Constants.SettingEntrySize;
                payload[offset]     = (byte)(((ushort)id >> 8) & 0xFF);
                payload[offset + 1] = (byte)((ushort)id & 0xFF);
                payload[offset + 2] = (byte)((value >> 24) & 0xFF);
                payload[offset + 3] = (byte)((value >> 16) & 0xFF);
                payload[offset + 4] = (byte)((value >> 8) & 0xFF);
                payload[offset + 5] = (byte)(value & 0xFF);
            }
            return payload;
        }

        /// <summary>
        /// Parse a SETTINGS frame payload into individual settings.
        /// </summary>
        public static List<(Http2SettingId Id, uint Value)> ParsePayload(byte[] payload)
        {
            if (payload.Length % Http2Constants.SettingEntrySize != 0)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    "SETTINGS payload length must be a multiple of 6");

            var result = new List<(Http2SettingId, uint)>(payload.Length / Http2Constants.SettingEntrySize);
            for (int i = 0; i < payload.Length; i += Http2Constants.SettingEntrySize)
            {
                var id = (Http2SettingId)((payload[i] << 8) | payload[i + 1]);
                uint value = ((uint)payload[i + 2] << 24) | ((uint)payload[i + 3] << 16) |
                             ((uint)payload[i + 4] << 8) | payload[i + 5];
                result.Add((id, value));
            }
            return result;
        }
    }
}
