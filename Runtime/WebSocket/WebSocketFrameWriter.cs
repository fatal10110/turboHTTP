using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// Writes masked client WebSocket frames to a stream.
    /// Not thread-safe; callers must serialize sends.
    /// </summary>
    public sealed class WebSocketFrameWriter : IDisposable
    {
        private const int DefaultMaskingChunkSize = 8 * 1024;
        private const int MaskKeyBatchByteSize = 256; // 64 mask keys * 4 bytes

        private readonly RandomNumberGenerator _rng;
        private readonly object _maskKeyLock = new object();
        private readonly byte[] _maskKeyBatchBuffer;
        private readonly byte[] _maskingChunkBuffer;
        private readonly byte[] _headerBuffer;

        private readonly int _fragmentationThreshold;
        private int _maskKeyBatchOffset;
        private int _disposed;

        public WebSocketFrameWriter(
            int fragmentationThreshold = WebSocketConstants.DefaultFragmentationThreshold,
            int maskingChunkSize = DefaultMaskingChunkSize)
        {
            if (fragmentationThreshold < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(fragmentationThreshold),
                    fragmentationThreshold,
                    "Fragmentation threshold must be at least 1 byte.");
            }

            if (maskingChunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maskingChunkSize),
                    maskingChunkSize,
                    "Masking chunk size must be at least 1 byte.");
            }

            _rng = RandomNumberGenerator.Create();
            if (_rng == null)
                throw new PlatformNotSupportedException("Random number generator is unavailable.");

            _fragmentationThreshold = fragmentationThreshold;
            _maskKeyBatchBuffer = new byte[MaskKeyBatchByteSize];
            _maskKeyBatchOffset = _maskKeyBatchBuffer.Length;
            _maskingChunkBuffer = new byte[maskingChunkSize];
            _headerBuffer = new byte[14];
        }

        public async Task WriteTextAsync(Stream stream, string message, CancellationToken ct)
        {
            ThrowIfDisposed();

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (message == null)
                throw new ArgumentNullException(nameof(message));

            int byteCount;
            try
            {
                byteCount = WebSocketConstants.StrictUtf8.GetByteCount(message);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Message is not valid UTF-8 encodable text.", nameof(message), ex);
            }

            if (byteCount == 0)
            {
                await WriteMessageAsync(stream, WebSocketOpcode.Text, ReadOnlyMemory<byte>.Empty, ct)
                    .ConfigureAwait(false);
                return;
            }

            byte[] payloadBuffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int written = WebSocketConstants.StrictUtf8.GetBytes(
                    message,
                    0,
                    message.Length,
                    payloadBuffer,
                    0);

                await WriteMessageAsync(
                    stream,
                    WebSocketOpcode.Text,
                    new ReadOnlyMemory<byte>(payloadBuffer, 0, written),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payloadBuffer);
            }
        }

        public Task WriteBinaryAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            return WriteMessageAsync(stream, WebSocketOpcode.Binary, payload, ct);
        }

        public Task WritePingAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            return WriteFrameAsync(stream, WebSocketOpcode.Ping, isFinal: true, payload, ct);
        }

        public Task WritePongAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            ThrowIfDisposed();
            return WriteFrameAsync(stream, WebSocketOpcode.Pong, isFinal: true, payload, ct);
        }

        public async Task WriteCloseAsync(
            Stream stream,
            WebSocketCloseCode code,
            string reason,
            CancellationToken ct)
        {
            ThrowIfDisposed();

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!WebSocketConstants.ValidateCloseCode((int)code, allowReservedLocal: false))
            {
                throw new WebSocketProtocolException(
                    WebSocketError.InvalidCloseCode,
                    "Close code is not valid for wire transmission.",
                    WebSocketCloseCode.ProtocolError);
            }

            reason = reason ?? string.Empty;

            int reasonBytes;
            int reasonChars;
            try
            {
                reasonBytes = WebSocketConstants.GetTruncatedCloseReasonByteCount(reason, out reasonChars);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Close reason is not valid UTF-8 encodable text.", nameof(reason), ex);
            }

            int payloadLength = 2 + reasonBytes;
            byte[] closePayload = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                BinaryPrimitives.WriteUInt16BigEndian(
                    new Span<byte>(closePayload, 0, 2),
                    (ushort)code);

                if (reasonBytes > 0)
                {
                    _ = WebSocketConstants.StrictUtf8.GetBytes(
                        reason,
                        0,
                        reasonChars,
                        closePayload,
                        2);
                }

                await WriteFrameAsync(
                    stream,
                    WebSocketOpcode.Close,
                    isFinal: true,
                    new ReadOnlyMemory<byte>(closePayload, 0, payloadLength),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(closePayload);
            }
        }

        /// <summary>
        /// Writes a text or binary message, fragmenting when payload exceeds the configured threshold.
        /// </summary>
        public async Task WriteMessageAsync(
            Stream stream,
            WebSocketOpcode opcode,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct)
        {
            ThrowIfDisposed();

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (opcode != WebSocketOpcode.Text && opcode != WebSocketOpcode.Binary)
            {
                throw new ArgumentException(
                    "Only text or binary opcodes are valid for message writes.",
                    nameof(opcode));
            }

            if (payload.Length <= _fragmentationThreshold)
            {
                await WriteFrameAsync(stream, opcode, isFinal: true, payload, ct)
                    .ConfigureAwait(false);
                return;
            }

            int offset = 0;
            bool first = true;
            while (offset < payload.Length)
            {
                int fragmentLength = Math.Min(_fragmentationThreshold, payload.Length - offset);
                bool isFinal = offset + fragmentLength >= payload.Length;
                var fragmentOpcode = first ? opcode : WebSocketOpcode.Continuation;

                await WriteFrameAsync(
                    stream,
                    fragmentOpcode,
                    isFinal,
                    payload.Slice(offset, fragmentLength),
                    ct).ConfigureAwait(false);

                offset += fragmentLength;
                first = false;
            }
        }

        /// <summary>
        /// Writes a single frame. Client frames are always masked.
        /// </summary>
        public async Task WriteFrameAsync(
            Stream stream,
            WebSocketOpcode opcode,
            bool isFinal,
            ReadOnlyMemory<byte> payload,
            CancellationToken ct)
        {
            ThrowIfDisposed();

            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            if (!WebSocketConstants.TryParseOpcode((byte)opcode, out _))
            {
                throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "Unsupported or reserved opcode.");
            }

            if (WebSocketFrame.IsControlOpcode(opcode))
            {
                if (!isFinal)
                {
                    throw new ArgumentException(
                        "Control frames must not be fragmented.",
                        nameof(isFinal));
                }

                if (payload.Length > WebSocketConstants.MaxControlFramePayloadLength)
                {
                    throw new ArgumentException(
                        "Control frame payload exceeds 125 bytes.",
                        nameof(payload));
                }
            }

            var maskKey = NextMaskKey();
            int headerLength = BuildHeader(_headerBuffer, opcode, isFinal, payload.Length, maskKey);

            await stream.WriteAsync(_headerBuffer, 0, headerLength, ct).ConfigureAwait(false);
            await WriteMaskedPayloadAsync(stream, payload, maskKey, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _rng.Dispose();
        }

        private async Task WriteMaskedPayloadAsync(
            Stream stream,
            ReadOnlyMemory<byte> payload,
            uint maskKey,
            CancellationToken ct)
        {
            if (payload.Length == 0)
                return;

            int offset = 0;
            while (offset < payload.Length)
            {
                int toCopy = Math.Min(_maskingChunkBuffer.Length, payload.Length - offset);
                payload.Slice(offset, toCopy).CopyTo(new Memory<byte>(_maskingChunkBuffer, 0, toCopy));

                ApplyMaskInPlace(_maskingChunkBuffer, toCopy, maskKey, offset);
                await stream.WriteAsync(_maskingChunkBuffer, 0, toCopy, ct).ConfigureAwait(false);

                offset += toCopy;
            }
        }

        private static int BuildHeader(
            byte[] buffer,
            WebSocketOpcode opcode,
            bool isFinal,
            int payloadLength,
            uint maskKey)
        {
            int offset = 0;
            buffer[offset++] = (byte)(((isFinal ? 1 : 0) << 7) | ((byte)opcode & 0x0F));

            if (payloadLength <= 125)
            {
                buffer[offset++] = (byte)(0x80 | payloadLength);
            }
            else if (payloadLength <= ushort.MaxValue)
            {
                buffer[offset++] = 0xFE; // MASK set + 126 length indicator
                BinaryPrimitives.WriteUInt16BigEndian(new Span<byte>(buffer, offset, 2), (ushort)payloadLength);
                offset += 2;
            }
            else
            {
                buffer[offset++] = 0xFF; // MASK set + 127 length indicator
                BinaryPrimitives.WriteUInt64BigEndian(new Span<byte>(buffer, offset, 8), (ulong)payloadLength);
                offset += 8;
            }

            BinaryPrimitives.WriteUInt32BigEndian(new Span<byte>(buffer, offset, 4), maskKey);
            offset += 4;

            return offset;
        }

        private uint NextMaskKey()
        {
            lock (_maskKeyLock)
            {
                if (_maskKeyBatchOffset >= _maskKeyBatchBuffer.Length)
                {
                    _rng.GetBytes(_maskKeyBatchBuffer);
                    _maskKeyBatchOffset = 0;
                }

                uint maskKey = BinaryPrimitives.ReadUInt32BigEndian(
                    new ReadOnlySpan<byte>(_maskKeyBatchBuffer, _maskKeyBatchOffset, 4));

                _maskKeyBatchOffset += 4;
                return maskKey;
            }
        }

        private static void ApplyMaskInPlace(byte[] buffer, int count, uint maskKey, int payloadOffset)
        {
            byte key0 = (byte)(maskKey >> 24);
            byte key1 = (byte)(maskKey >> 16);
            byte key2 = (byte)(maskKey >> 8);
            byte key3 = (byte)maskKey;

            for (int i = 0; i < count; i++)
            {
                switch ((payloadOffset + i) & 0x3)
                {
                    case 0:
                        buffer[i] ^= key0;
                        break;
                    case 1:
                        buffer[i] ^= key1;
                        break;
                    case 2:
                        buffer[i] ^= key2;
                        break;
                    default:
                        buffer[i] ^= key3;
                        break;
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(WebSocketFrameWriter));
        }
    }
}
