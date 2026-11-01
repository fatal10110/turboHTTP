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

        // A frame payload is written in slices of at most this size so a large DATA frame (the peer's
        // SETTINGS_MAX_FRAME_SIZE may reach 2^24-1) records progress per slice. 16384 is the smallest
        // legal SETTINGS_MAX_FRAME_SIZE (RFC 9113 Section 6.5.2), so default-sized frames are one slice.
        internal const int WriteProgressSliceBytes = 16384;

        // Http2MonotonicClock timestamp of the last progress of the frame write in flight (set before
        // each header, payload-slice and flush write), 0 while no frame write is in flight. Written by
        // the single writer (serialized by the connection's _writeLock), read by the connection's
        // maintenance loop: Volatile on both sides (ARM64 / 32-bit IL2CPP memory model).
        private long _writeProgressTimestamp;

        public Http2FrameCodec(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <summary>
        /// True when a frame write is in flight and its last progress (a header, payload-slice or
        /// flush write starting) is at least <paramref name="timeoutMilliseconds"/> before
        /// <paramref name="nowTimestamp"/> (an <see cref="Http2MonotonicClock"/> timestamp). Safe to
        /// call from any thread.
        /// </summary>
        internal bool IsWriteStalled(long nowTimestamp, long timeoutMilliseconds)
        {
            long lastProgress = Volatile.Read(ref _writeProgressTimestamp);
            return lastProgress != 0 &&
                   Http2MonotonicClock.HasElapsedMilliseconds(lastProgress, nowTimestamp, timeoutMilliseconds);
        }

        private void MarkWriteProgress()
        {
            long now = Http2MonotonicClock.GetTimestamp();
            Volatile.Write(ref _writeProgressTimestamp, now == 0 ? 1 : now);
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

            await WriteFrameAsync(
                    frame.Type,
                    frame.Flags,
                    frame.StreamId,
                    new ReadOnlyMemory<byte>(payload, 0, payloadLength),
                    ct,
                    flush)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Write a single HTTP/2 frame payload without requiring an intermediate byte[] copy.
        /// </summary>
        public async Task WriteFrameAsync(
            Http2FrameType type,
            Http2FrameFlags flags,
            int streamId,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct,
            bool flush = true)
        {
            int payloadLength = payload.Length;
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength), payloadLength,
                    "Frame length must be non-negative.");

            // Reuse pre-allocated header buffer (writes serialized by _writeLock)
            _writeHeaderBuffer[0] = (byte)((payloadLength >> 16) & 0xFF);
            _writeHeaderBuffer[1] = (byte)((payloadLength >> 8) & 0xFF);
            _writeHeaderBuffer[2] = (byte)(payloadLength & 0xFF);
            _writeHeaderBuffer[3] = (byte)type;
            _writeHeaderBuffer[4] = (byte)flags;
            _writeHeaderBuffer[5] = (byte)((streamId >> 24) & 0x7F); // mask reserved bit
            _writeHeaderBuffer[6] = (byte)((streamId >> 16) & 0xFF);
            _writeHeaderBuffer[7] = (byte)((streamId >> 8) & 0xFF);
            _writeHeaderBuffer[8] = (byte)(streamId & 0xFF);

            MarkWriteProgress();
            try
            {
                await _stream.WriteAsync(_writeHeaderBuffer, 0, Http2Constants.FrameHeaderSize, ct)
                    .ConfigureAwait(false);

                int offset = 0;
                while (offset < payloadLength)
                {
                    int slice = Math.Min(WriteProgressSliceBytes, payloadLength - offset);
                    MarkWriteProgress();
                    await _stream.WriteAsync(payload.Slice(offset, slice), ct).ConfigureAwait(false);
                    offset += slice;
                }

                if (flush)
                {
                    MarkWriteProgress();
                    await _stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                Volatile.Write(ref _writeProgressTimestamp, 0);
            }
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
