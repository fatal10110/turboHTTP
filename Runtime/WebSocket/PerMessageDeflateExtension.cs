using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Threading;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// RFC 7692 permessage-deflate extension (v1 no_context_takeover-only mode).
    /// </summary>
    public sealed class PerMessageDeflateExtension : IWebSocketExtension
    {
        private static readonly byte[] DeflateTail = new byte[] { 0x00, 0x00, 0xFF, 0xFF };

        // 1 once this runtime's DeflateStream.Flush was seen not to sync-flush (Unity Mono): its
        // messages then end with a final block (#217). Written and read with Volatile.
        private static int s_flushIsNotSyncFlush;

        private readonly PerMessageDeflateOptions _options;
        private readonly int _maxMessageSize;

        private bool _disposed;
        private bool _isNegotiated;
        private bool _clientNoContextTakeover;

        public PerMessageDeflateExtension(
            PerMessageDeflateOptions options = null,
            int maxMessageSize = WebSocketConstants.DefaultMaxMessageSize)
        {
            if (maxMessageSize < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMessageSize),
                    maxMessageSize,
                    "MaxMessageSize must be positive.");
            }

            _options = options ?? PerMessageDeflateOptions.Default;
            _maxMessageSize = maxMessageSize;
        }

        public string Name => "permessage-deflate";

        public byte RsvBitMask => 0x40;

        public IReadOnlyList<WebSocketExtensionOffer> BuildOffers()
        {
            ThrowIfDisposed();

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // The inbound inflater is created per message, so server context takeover cannot be
            // honoured: always ask for server_no_context_takeover (RFC 7692 section 7.1.1.1, #82).
            parameters["server_no_context_takeover"] = null;

            if (_options.ClientNoContextTakeover)
                parameters["client_no_context_takeover"] = null;

            // client_max_window_bits is never offered: DeflateStream always compresses with a 15-bit
            // window, so a smaller client window could not be honoured (RFC 7692 section 7.1.2.2).
            parameters["server_max_window_bits"] = _options.ServerMaxWindowBits.ToString(
                System.Globalization.CultureInfo.InvariantCulture);

            return new[] { new WebSocketExtensionOffer(Name, parameters) };
        }

        public bool AcceptNegotiation(WebSocketExtensionParameters serverParams)
        {
            ThrowIfDisposed();

            if (serverParams == null)
                return false;

            if (!string.Equals(serverParams.ExtensionToken, Name, StringComparison.OrdinalIgnoreCase))
                return false;

            bool serverNoContextTakeover = false;
            bool clientNoContextTakeover = false;
            int serverMaxWindowBits = _options.ServerMaxWindowBits;

            foreach (var parameter in serverParams.Parameters)
            {
                switch (parameter.Key.ToLowerInvariant())
                {
                    case "server_no_context_takeover":
                        if (parameter.Value != null)
                            return false;

                        serverNoContextTakeover = true;
                        break;

                    case "client_no_context_takeover":
                        if (parameter.Value != null)
                            return false;

                        clientNoContextTakeover = true;
                        break;

                    case "server_max_window_bits":
                        if (!TryParseWindowBits(parameter.Value, out serverMaxWindowBits))
                            return false;
                        break;

                    case "client_max_window_bits":
                        // Not offered, so the response must not contain it (RFC 7692 section 7.1.2.2).
                        return false;

                    default:
                        return false;
                }
            }

            // Inbound context takeover is not implemented (a fresh inflater per message), so the server
            // must agree to reset its context after every message (RFC 7692 section 7.1.1.1).
            if (!serverNoContextTakeover)
                return false;

            // Any window up to 15 bits inflates with the 15-bit inflater; only a value above the offer
            // violates RFC 7692 section 7.1.2.1.
            if (serverMaxWindowBits > _options.ServerMaxWindowBits)
                return false;

            _clientNoContextTakeover = clientNoContextTakeover;
            _isNegotiated = true;
            return true;
        }

        public IMemoryOwner<byte> TransformOutbound(
            ReadOnlyMemory<byte> payload,
            WebSocketOpcode opcode,
            out byte rsvBits)
        {
            ThrowIfDisposed();

            rsvBits = 0;

            if (!_isNegotiated)
                return null;

            if (opcode != WebSocketOpcode.Text && opcode != WebSocketOpcode.Binary)
                return null;

            if (payload.Length < _options.CompressionThreshold)
                return null;

            // Without client_no_context_takeover in the agreed parameters the server may inflate all
            // messages with one retained stream (RFC 7692 section 7.2.2), which cannot continue past a
            // final block. Where Flush cannot end a message without one, such messages go uncompressed
            // (RSV1 unset, RFC 7692 section 6).
            bool finalBlockAllowed = _clientNoContextTakeover;
            if (!finalBlockAllowed && Volatile.Read(ref s_flushIsNotSyncFlush) != 0)
                return null;

            try
            {
                var compressedOwner = Compress(payload, finalBlockAllowed);
                if (compressedOwner == null)
                    return null;

                rsvBits = RsvBitMask;
                return compressedOwner;
            }
            catch (WebSocketException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.CompressionFailed,
                    "Failed to compress outgoing message payload.",
                    ex);
            }
        }

        public IMemoryOwner<byte> TransformInbound(
            ReadOnlyMemory<byte> payload,
            WebSocketOpcode opcode,
            byte rsvBits)
        {
            ThrowIfDisposed();

            if (!_isNegotiated)
                return null;

            if (opcode != WebSocketOpcode.Text && opcode != WebSocketOpcode.Binary)
                return null;

            if ((rsvBits & RsvBitMask) == 0)
                return null;

            try
            {
                return Decompress(payload);
            }
            catch (WebSocketException)
            {
                throw;
            }
            catch (InvalidDataException ex)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressionFailed,
                    "Failed to decompress incoming message payload.",
                    ex);
            }
            catch (IOException ex)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressionFailed,
                    "Failed to decompress incoming message payload.",
                    ex);
            }
            catch (Exception ex)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressionFailed,
                    "Failed to decompress incoming message payload.",
                    ex);
            }
        }

        public void Reset()
        {
            // v1 no_context_takeover mode has no retained state between messages.
        }

        public void Dispose()
        {
            _disposed = true;
        }

        private IMemoryOwner<byte> Compress(ReadOnlyMemory<byte> payload, bool finalBlockAllowed)
        {
            using var output = new MemoryStream(payload.Length + 32);
            long syncFlushLength;

            using (var deflate = new DeflateStream(
                output,
                MapCompressionLevel(_options.CompressionLevel),
                leaveOpen: true))
            {
                if (payload.Length > 0)
                {
                    if (MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment) && segment.Array != null)
                    {
                        deflate.Write(segment.Array, segment.Offset, segment.Count);
                    }
                    else
                    {
                        deflate.Write(payload.Span);
                    }
                }

                // Sync flush: the data now ends with an empty stored block (00 00 FF FF), RFC 7692
                // section 7.2.1 step 2. Disposing the stream below appends a final block (BFINAL=1).
                deflate.Flush();
                syncFlushLength = output.Length;
            }

            byte[] compressed = output.TryGetBuffer(out ArraySegment<byte> buffer) && buffer.Array != null && buffer.Offset == 0
                ? buffer.Array
                : output.ToArray();

            // The message ends at the sync flush (#217): a server that keeps its LZ77 window across
            // messages (no client_no_context_takeover agreed, RFC 7692 section 7.2.2) inflates every
            // message with one stream and cannot continue past a final block.
            int compressedLength;
            bool appendStoredBlockHeader = false;
            if (HasTail(compressed, (int)syncFlushLength, DeflateTail))
            {
                compressedLength = (int)syncFlushLength - DeflateTail.Length;
            }
            else
            {
                // An empty payload may flush nothing at all, so only data proves the runtime's behaviour.
                if (payload.Length > 0)
                    Volatile.Write(ref s_flushIsNotSyncFlush, 1);
                if (!finalBlockAllowed)
                    return null;

                // The server resets its inflater per message: keep the final block and add the empty
                // stored block RFC 7692 section 7.2.1 step 2 requires, minus its 00 00 FF FF (7.2.3.4).
                compressedLength = (int)output.Length;
                if (HasTail(compressed, compressedLength, DeflateTail))
                    compressedLength -= DeflateTail.Length;
                else
                    appendStoredBlockHeader = true;
            }

            int messageLength = compressedLength + (appendStoredBlockHeader ? 1 : 0);
            var owner = ArrayPoolMemoryOwner<byte>.Rent(messageLength);
            if (compressedLength > 0)
                new ReadOnlyMemory<byte>(compressed, 0, compressedLength).CopyTo(owner.Memory);
            if (appendStoredBlockHeader)
                owner.Memory.Span[compressedLength] = 0x00;

            return owner;
        }

        private IMemoryOwner<byte> Decompress(ReadOnlyMemory<byte> payload)
        {
            int inputLength;
            try
            {
                inputLength = checked(payload.Length + DeflateTail.Length);
            }
            catch (OverflowException ex)
            {
                throw new WebSocketException(
                    WebSocketError.DecompressionFailed,
                    "Compressed payload length overflowed while preparing decompression input.",
                    ex);
            }

            byte[] compressedWithTail = ArrayPool<byte>.Shared.Rent(inputLength);
            byte[] chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);

            try
            {
                payload.CopyTo(new Memory<byte>(compressedWithTail, 0, payload.Length));
                new ReadOnlyMemory<byte>(DeflateTail).CopyTo(
                    new Memory<byte>(compressedWithTail, payload.Length, DeflateTail.Length));

                using var input = new MemoryStream(compressedWithTail, 0, inputLength, writable: false);
                using var inflate = new DeflateStream(input, CompressionMode.Decompress, leaveOpen: true);
                using var output = new MemoryStream();

                int totalRead = 0;
                while (true)
                {
                    int read = inflate.Read(chunk, 0, chunk.Length);
                    if (read <= 0)
                        break;

                    totalRead = checked(totalRead + read);
                    if (totalRead > _maxMessageSize)
                    {
                        throw new WebSocketException(
                            WebSocketError.DecompressedMessageTooLarge,
                            "Decompressed payload exceeds configured MaxMessageSize.");
                    }

                    output.Write(chunk, 0, read);
                }

                if (!output.TryGetBuffer(out ArraySegment<byte> decompressedSegment) ||
                    decompressedSegment.Array == null)
                {
                    byte[] decompressedFallback = output.ToArray();
                    var fallbackOwner = ArrayPoolMemoryOwner<byte>.Rent(decompressedFallback.Length);
                    if (decompressedFallback.Length > 0)
                    {
                        new ReadOnlyMemory<byte>(decompressedFallback, 0, decompressedFallback.Length)
                            .CopyTo(fallbackOwner.Memory);
                    }

                    return fallbackOwner;
                }

                int decompressedLength = (int)output.Length;
                var owner = ArrayPoolMemoryOwner<byte>.Rent(decompressedLength);
                if (decompressedLength > 0)
                {
                    new ReadOnlyMemory<byte>(
                        decompressedSegment.Array,
                        decompressedSegment.Offset,
                        decompressedLength).CopyTo(owner.Memory);
                }

                return owner;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
                ArrayPool<byte>.Shared.Return(compressedWithTail);
            }
        }

        private static bool HasTail(byte[] buffer, int length, byte[] tail)
        {
            if (buffer == null || tail == null)
                return false;

            if (length < tail.Length)
                return false;

            int start = length - tail.Length;
            for (int i = 0; i < tail.Length; i++)
            {
                if (buffer[start + i] != tail[i])
                    return false;
            }

            return true;
        }

        private static bool TryParseWindowBits(string value, out int bits)
        {
            bits = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (!int.TryParse(value, out bits))
                return false;

            return bits >= 8 && bits <= 15;
        }

        private static CompressionLevel MapCompressionLevel(int level)
        {
            if (level <= 0)
                return CompressionLevel.NoCompression;

            if (level <= 3)
                return CompressionLevel.Fastest;

            return CompressionLevel.Optimal;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PerMessageDeflateExtension));
        }
    }
}
