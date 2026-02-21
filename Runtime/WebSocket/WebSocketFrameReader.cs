using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Reads individual WebSocket frames from a stream.
    /// This reader is protocol-stateless; callers provide current fragmentation state.
    /// </summary>
    public sealed class WebSocketFrameReader
    {
        private readonly int _maxFrameSize;
        private readonly byte _allowedRsvMask;
        private readonly bool _rejectMaskedServerFrames;

        public WebSocketFrameReader(
            int maxFrameSize = WebSocketConstants.DefaultMaxFrameSize,
            byte allowedRsvMask = 0,
            bool rejectMaskedServerFrames = true)
        {
            if (maxFrameSize < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxFrameSize),
                    maxFrameSize,
                    "Max frame size must be positive.");
            }

            if ((allowedRsvMask & ~0x70) != 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(allowedRsvMask),
                    allowedRsvMask,
                    "Only RSV bits (0x70) can be enabled.");
            }

            _maxFrameSize = maxFrameSize;
            _allowedRsvMask = (byte)(allowedRsvMask & 0x70);
            _rejectMaskedServerFrames = rejectMaskedServerFrames;
        }

        /// <summary>
        /// Reads a single frame. Returns null on clean EOF before any header bytes are read.
        /// </summary>
        /// <param name="stream">Connected network stream.</param>
        /// <param name="fragmentedMessageInProgress">
        /// True when a fragmented text/binary message is currently awaiting continuation frames.
        /// </param>
        public async Task<WebSocketFrameReadLease> ReadAsync(
            Stream stream,
            bool fragmentedMessageInProgress,
            CancellationToken ct)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            byte[] headerBuffer = ArrayPool<byte>.Shared.Rent(14);
            byte[] payloadBuffer = null;

            try
            {
                bool hasHeader = await ReadExactAsync(stream, headerBuffer, 0, 2, ct).ConfigureAwait(false);
                if (!hasHeader)
                    return null;

                byte first = headerBuffer[0];
                byte second = headerBuffer[1];
                int headerByteCount = 2;

                bool isFinal = (first & 0x80) != 0;
                byte rsvBits = (byte)(first & 0x70);
                if ((rsvBits & (byte)~_allowedRsvMask) != 0)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.ProtocolViolation,
                        "RSV bits set without a negotiated extension.");
                }

                byte opcodeValue = (byte)(first & 0x0F);
                if (WebSocketConstants.IsReservedOpcode(opcodeValue))
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.ReservedOpcode,
                        "Received reserved WebSocket opcode.");
                }

                if (!WebSocketConstants.TryParseOpcode(opcodeValue, out var opcode))
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.InvalidFrame,
                        "Received invalid WebSocket opcode.");
                }

                bool isMasked = (second & 0x80) != 0;
                ulong payloadLength = (ulong)(second & 0x7F);

                if (payloadLength == 126)
                {
                    if (!await ReadExactAsync(stream, headerBuffer, 0, 2, ct).ConfigureAwait(false))
                        throw new IOException("Unexpected end of stream while reading extended payload length.");

                    payloadLength = BinaryPrimitives.ReadUInt16BigEndian(
                        new ReadOnlySpan<byte>(headerBuffer, 0, 2));
                    headerByteCount += 2;
                }
                else if (payloadLength == 127)
                {
                    if (!await ReadExactAsync(stream, headerBuffer, 0, 8, ct).ConfigureAwait(false))
                        throw new IOException("Unexpected end of stream while reading extended payload length.");

                    payloadLength = BinaryPrimitives.ReadUInt64BigEndian(
                        new ReadOnlySpan<byte>(headerBuffer, 0, 8));

                    if ((payloadLength & 0x8000000000000000UL) != 0)
                    {
                        throw new WebSocketProtocolException(
                            WebSocketError.PayloadLengthOverflow,
                            "Invalid 64-bit payload length. Most significant bit must be zero.");
                    }

                    headerByteCount += 8;
                }

                if (payloadLength > (ulong)_maxFrameSize)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.FrameTooLarge,
                        "Frame payload exceeds configured max frame size.",
                        WebSocketCloseCode.MessageTooBig);
                }

                if (payloadLength > int.MaxValue)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.PayloadLengthOverflow,
                        "Payload length exceeds supported range.");
                }

                if (WebSocketFrame.IsControlOpcode(opcode))
                {
                    if (!isFinal)
                    {
                        throw new WebSocketProtocolException(
                            WebSocketError.ProtocolViolation,
                            "Control frame must not be fragmented.");
                    }

                    if (payloadLength > WebSocketConstants.MaxControlFramePayloadLength)
                    {
                        throw new WebSocketProtocolException(
                            WebSocketError.InvalidFrame,
                            "Control frame payload exceeds 125 bytes.");
                    }
                }

                if (opcode == WebSocketOpcode.Continuation && !fragmentedMessageInProgress)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.UnexpectedContinuation,
                        "Received continuation frame without an active fragmented message.");
                }

                if ((opcode == WebSocketOpcode.Text || opcode == WebSocketOpcode.Binary) &&
                    fragmentedMessageInProgress)
                {
                    throw new WebSocketProtocolException(
                        WebSocketError.ProtocolViolation,
                        "Received a new data frame while a fragmented message is in progress.");
                }

                uint maskKey = 0;
                if (isMasked)
                {
                    if (_rejectMaskedServerFrames)
                    {
                        throw new WebSocketProtocolException(
                            WebSocketError.MaskedServerFrame,
                            "Server-to-client frame is masked, which violates RFC 6455.");
                    }

                    if (!await ReadExactAsync(stream, headerBuffer, 0, 4, ct).ConfigureAwait(false))
                        throw new IOException("Unexpected end of stream while reading mask key.");

                    maskKey = BinaryPrimitives.ReadUInt32BigEndian(
                        new ReadOnlySpan<byte>(headerBuffer, 0, 4));
                    headerByteCount += 4;
                }

                int payloadLengthInt = (int)payloadLength;
                int frameByteCount = checked(headerByteCount + payloadLengthInt);
                if (payloadLengthInt > 0)
                {
                    payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLengthInt);
                    if (!await ReadExactAsync(stream, payloadBuffer, 0, payloadLengthInt, ct).ConfigureAwait(false))
                        throw new IOException("Unexpected end of stream while reading frame payload.");

                    if (isMasked)
                        ApplyMask(payloadBuffer, payloadLengthInt, maskKey);

                    var frameWithPayload = new WebSocketFrame(
                        opcode,
                        isFinal,
                        isMasked,
                        maskKey,
                        new ReadOnlyMemory<byte>(payloadBuffer, 0, payloadLengthInt),
                        rsvBits);

                    return new WebSocketFrameReadLease(
                        frameWithPayload,
                        payloadBuffer,
                        payloadLengthInt,
                        frameByteCount);
                }

                var frame = new WebSocketFrame(
                    opcode,
                    isFinal,
                    isMasked,
                    maskKey,
                    ReadOnlyMemory<byte>.Empty,
                    rsvBits);

                return new WebSocketFrameReadLease(frame, null, 0, frameByteCount);
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("WebSocket frame read was canceled.", ct);
            }
            catch (IOException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("WebSocket frame read was canceled.", ct);
            }
            catch
            {
                if (payloadBuffer != null)
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                throw;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(headerBuffer);
            }
        }

        private static async Task<bool> ReadExactAsync(
            Stream stream,
            byte[] buffer,
            int offset,
            int count,
            CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(
                    buffer,
                    offset + totalRead,
                    count - totalRead,
                    ct).ConfigureAwait(false);

                if (read == 0)
                {
                    if (totalRead == 0)
                        return false;

                    throw new IOException("Unexpected end of stream while reading WebSocket frame.");
                }

                totalRead += read;
            }

            return true;
        }

        private static void ApplyMask(byte[] payload, int payloadLength, uint maskKey)
        {
            byte key0 = (byte)(maskKey >> 24);
            byte key1 = (byte)(maskKey >> 16);
            byte key2 = (byte)(maskKey >> 8);
            byte key3 = (byte)maskKey;

            for (int i = 0; i < payloadLength; i += 4)
            {
                payload[i] ^= key0;
                if (i + 1 < payloadLength) payload[i + 1] ^= key1;
                if (i + 2 < payloadLength) payload[i + 2] ^= key2;
                if (i + 3 < payloadLength) payload[i + 3] ^= key3;
            }
        }
    }

    /// <summary>
    /// Leases a pooled payload buffer for a parsed frame.
    /// </summary>
    public sealed class WebSocketFrameReadLease : IDisposable
    {
        private byte[] _payloadBuffer;
        private int _payloadLength;

        internal WebSocketFrameReadLease(
            WebSocketFrame frame,
            byte[] payloadBuffer,
            int payloadLength,
            int frameByteCount)
        {
            Frame = frame;
            _payloadBuffer = payloadBuffer;
            _payloadLength = payloadLength;
            FrameByteCount = frameByteCount;
        }

        public WebSocketFrame Frame { get; }

        public int FrameByteCount { get; }

        public void Dispose()
        {
            var buffer = _payloadBuffer;
            if (buffer == null)
                return;

            _payloadBuffer = null;
            _payloadLength = 0;
            ArrayPool<byte>.Shared.Return(buffer);
        }

        internal byte[] DetachPayloadBuffer(out int length)
        {
            var buffer = _payloadBuffer;
            length = _payloadLength;
            _payloadBuffer = null;
            _payloadLength = 0;
            return buffer;
        }
    }
}
