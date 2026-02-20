using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Transport.Http2
{
    /// <summary>
    /// Lease object for frame payload buffers rented from ArrayPool.
    /// Dispose returns the rented payload buffer to the pool.
    /// </summary>
    internal sealed class Http2FrameReadLease : IDisposable
    {
        public Http2Frame Frame { get; }
        private byte[] _rentedPayload;

        internal Http2FrameReadLease(Http2Frame frame, byte[] rentedPayload)
        {
            Frame = frame;
            _rentedPayload = rentedPayload;
        }

        public void Dispose()
        {
            var buffer = _rentedPayload;
            if (buffer != null)
            {
                _rentedPayload = null;
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

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
        /// Uses reusable header buffer and materializes exact-size payload for compatibility.
        /// </summary>
        public async Task<Http2Frame> ReadFrameAsync(int maxFrameSize, CancellationToken ct)
        {
            using (var frameLease = await ReadFrameLeaseAsync(maxFrameSize, ct))
            {
                var frame = frameLease.Frame;
                if (frame.Length == 0)
                    return frame;

                // Compatibility API: materialize exact-size payload for callers that keep frames around.
                var payload = new byte[frame.Length];
                Buffer.BlockCopy(frame.Payload, 0, payload, 0, frame.Length);
                return new Http2Frame
                {
                    Length = frame.Length,
                    Type = frame.Type,
                    Flags = frame.Flags,
                    StreamId = frame.StreamId,
                    Payload = payload
                };
            }
        }

        /// <summary>
        /// Read a single HTTP/2 frame and lease pooled payload storage.
        /// Callers must Dispose the returned lease after consuming the frame.
        /// </summary>
        public async Task<Http2FrameReadLease> ReadFrameLeaseAsync(int maxFrameSize, CancellationToken ct)
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
                try
                {
                    rentedBuffer = ArrayPool<byte>.Shared.Rent(length);
                    await ReadExactAsync(rentedBuffer, length, ct);
                    payload = rentedBuffer;
                }
                catch
                {
                    if (rentedBuffer != null)
                        ArrayPool<byte>.Shared.Return(rentedBuffer);
                    throw;
                }
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            return new Http2FrameReadLease(new Http2Frame
            {
                Length = length,
                Type = type,
                Flags = flags,
                StreamId = streamId,
                Payload = payload
            }, rentedBuffer);
        }

        /// <summary>
        /// Write a single HTTP/2 frame to the stream.
        /// Uses reusable header buffer (writes are serialized under lock).
        /// </summary>
        public async Task WriteFrameAsync(Http2Frame frame, CancellationToken ct, bool flush = true)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            int payloadLength = frame.Length;
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(frame.Length), frame.Length,
                    "Frame length must be non-negative.");

            var payload = frame.Payload ?? Array.Empty<byte>();
            if (payloadLength > payload.Length)
                throw new ArgumentException(
                    "Frame length exceeds payload buffer length.",
                    nameof(frame));

            if (payloadLength == 0)
            {
                // Reuse pre-allocated header buffer (writes serialized by _writeLock)
                _writeHeaderBuffer[0] = 0;
                _writeHeaderBuffer[1] = 0;
                _writeHeaderBuffer[2] = 0;
                _writeHeaderBuffer[3] = (byte)frame.Type;
                _writeHeaderBuffer[4] = (byte)frame.Flags;
                _writeHeaderBuffer[5] = (byte)((frame.StreamId >> 24) & 0x7F); // mask reserved bit
                _writeHeaderBuffer[6] = (byte)((frame.StreamId >> 16) & 0xFF);
                _writeHeaderBuffer[7] = (byte)((frame.StreamId >> 8) & 0xFF);
                _writeHeaderBuffer[8] = (byte)(frame.StreamId & 0xFF);

                await _stream.WriteAsync(_writeHeaderBuffer, 0, Http2Constants.FrameHeaderSize, ct);
            }
            else
            {
                var writeBuffer = ArrayPool<byte>.Shared.Rent(Http2Constants.FrameHeaderSize + payloadLength);
                try
                {
                    writeBuffer[0] = (byte)((payloadLength >> 16) & 0xFF);
                    writeBuffer[1] = (byte)((payloadLength >> 8) & 0xFF);
                    writeBuffer[2] = (byte)(payloadLength & 0xFF);
                    writeBuffer[3] = (byte)frame.Type;
                    writeBuffer[4] = (byte)frame.Flags;
                    writeBuffer[5] = (byte)((frame.StreamId >> 24) & 0x7F); // mask reserved bit
                    writeBuffer[6] = (byte)((frame.StreamId >> 16) & 0xFF);
                    writeBuffer[7] = (byte)((frame.StreamId >> 8) & 0xFF);
                    writeBuffer[8] = (byte)(frame.StreamId & 0xFF);
                    Buffer.BlockCopy(payload, 0, writeBuffer, Http2Constants.FrameHeaderSize, payloadLength);

                    await _stream.WriteAsync(
                        writeBuffer,
                        0,
                        Http2Constants.FrameHeaderSize + payloadLength,
                        ct);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(writeBuffer);
                }
            }

            if (flush)
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
