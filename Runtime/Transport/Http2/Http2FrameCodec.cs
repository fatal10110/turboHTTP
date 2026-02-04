using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Reads and writes HTTP/2 frames from/to a stream. RFC 7540 Section 4.1.
    /// NOT thread-safe — callers must serialize access (write lock for writes,
    /// single read loop for reads).
    /// </summary>
    internal class Http2FrameCodec
    {
        private readonly Stream _stream;

        public Http2FrameCodec(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// Read a single HTTP/2 frame from the stream.
        /// </summary>
        public async Task<Http2Frame> ReadFrameAsync(int maxFrameSize, CancellationToken ct)
        {
            var header = new byte[Http2Constants.FrameHeaderSize];
            await ReadExactAsync(header, Http2Constants.FrameHeaderSize, ct);

            int length = (header[0] << 16) | (header[1] << 8) | header[2];
            var type = (Http2FrameType)header[3];
            var flags = (Http2FrameFlags)header[4];
            int streamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) |
                           (header[7] << 8) | header[8];

            if (length > maxFrameSize)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    $"Frame length {length} exceeds maximum {maxFrameSize}");

            byte[] payload;
            if (length > 0)
            {
                payload = new byte[length];
                await ReadExactAsync(payload, length, ct);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return new Http2Frame
            {
                Length = length,
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Payload = payload
            };
        }

        /// <summary>
        /// Write a single HTTP/2 frame to the stream.
        /// </summary>
        public async Task WriteFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            int payloadLength = frame.Payload?.Length ?? 0;

            var header = new byte[Http2Constants.FrameHeaderSize];
            header[0] = (byte)((payloadLength >> 16) & 0xFF);
            header[1] = (byte)((payloadLength >> 8) & 0xFF);
            header[2] = (byte)(payloadLength & 0xFF);
            header[3] = (byte)frame.Type;
            header[4] = (byte)frame.Flags;
            header[5] = (byte)((frame.StreamId >> 24) & 0x7F); // mask reserved bit
            header[6] = (byte)((frame.StreamId >> 16) & 0xFF);
            header[7] = (byte)((frame.StreamId >> 8) & 0xFF);
            header[8] = (byte)(frame.StreamId & 0xFF);

            await _stream.WriteAsync(header, 0, Http2Constants.FrameHeaderSize, ct);

            if (payloadLength > 0)
                await _stream.WriteAsync(frame.Payload, 0, payloadLength, ct);

            await _stream.FlushAsync(ct);
        }

        /// <summary>
        /// Write the HTTP/2 connection preface. RFC 7540 Section 3.5.
        /// </summary>
        public async Task WritePrefaceAsync(CancellationToken ct)
        {
            await _stream.WriteAsync(Http2Constants.ConnectionPreface, 0,
                Http2Constants.ConnectionPreface.Length, ct);
            await _stream.FlushAsync(ct);
        }

        private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken ct)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await _stream.ReadAsync(buffer, offset, count - offset, ct);
                if (read == 0)
                    throw new IOException("Unexpected end of HTTP/2 stream");
                offset += read;
            }
        }
    }
}
