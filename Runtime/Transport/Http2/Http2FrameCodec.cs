using System;
using System.Buffers;
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

        // Reusable buffer for 9-byte frame headers (single-threaded read loop)
        private readonly byte[] _readHeaderBuffer = new byte[Http2Constants.FrameHeaderSize];
        // Reusable buffer for 9-byte frame headers (write operations under lock)
        private readonly byte[] _writeHeaderBuffer = new byte[Http2Constants.FrameHeaderSize];

        public Http2FrameCodec(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// Read a single HTTP/2 frame from the stream.
        /// Uses reusable header buffer; payload uses ArrayPool for frames > threshold.
        /// </summary>
        public async Task<Http2Frame> ReadFrameAsync(int maxFrameSize, CancellationToken ct)
        {
            // Reuse pre-allocated header buffer (single-threaded read loop)
            await ReadExactAsync(_readHeaderBuffer, Http2Constants.FrameHeaderSize, ct);

            int length = (_readHeaderBuffer[0] << 16) | (_readHeaderBuffer[1] << 8) | _readHeaderBuffer[2];
            var type = (Http2FrameType)_readHeaderBuffer[3];
            var flags = (Http2FrameFlags)_readHeaderBuffer[4];
            int streamId = ((_readHeaderBuffer[5] & 0x7F) << 24) | (_readHeaderBuffer[6] << 16) |
                           (_readHeaderBuffer[7] << 8) | _readHeaderBuffer[8];

            if (length > maxFrameSize)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                    $"Frame length {length} exceeds maximum {maxFrameSize}");

            byte[] payload;
            byte[] rentedBuffer = null;
            if (length > 0)
            {
                // Use ArrayPool for larger payloads to reduce GC pressure
                // Threshold: 256 bytes - small control frames allocate directly,
                // larger DATA/HEADERS frames use pool
                if (length > 256)
                {
                    rentedBuffer = ArrayPool<byte>.Shared.Rent(length);
                    await ReadExactAsync(rentedBuffer, length, ct);
                    // Copy to exact-size array since callers retain the payload
                    payload = new byte[length];
                    Buffer.BlockCopy(rentedBuffer, 0, payload, 0, length);
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                else
                {
                    payload = new byte[length];
                    await ReadExactAsync(payload, length, ct);
                }
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
        /// Uses reusable header buffer (writes are serialized under lock).
        /// </summary>
        public async Task WriteFrameAsync(Http2Frame frame, CancellationToken ct)
        {
            int payloadLength = frame.Payload?.Length ?? 0;

            // Reuse pre-allocated header buffer (writes serialized by _writeLock)
            _writeHeaderBuffer[0] = (byte)((payloadLength >> 16) & 0xFF);
            _writeHeaderBuffer[1] = (byte)((payloadLength >> 8) & 0xFF);
            _writeHeaderBuffer[2] = (byte)(payloadLength & 0xFF);
            _writeHeaderBuffer[3] = (byte)frame.Type;
            _writeHeaderBuffer[4] = (byte)frame.Flags;
            _writeHeaderBuffer[5] = (byte)((frame.StreamId >> 24) & 0x7F); // mask reserved bit
            _writeHeaderBuffer[6] = (byte)((frame.StreamId >> 16) & 0xFF);
            _writeHeaderBuffer[7] = (byte)((frame.StreamId >> 8) & 0xFF);
            _writeHeaderBuffer[8] = (byte)(frame.StreamId & 0xFF);

            await _stream.WriteAsync(_writeHeaderBuffer, 0, Http2Constants.FrameHeaderSize, ct);

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
