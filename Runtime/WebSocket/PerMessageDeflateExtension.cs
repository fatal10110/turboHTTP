using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace TurboHTTP.WebSocket
{
    /// <summary>
    /// RFC 7692 permessage-deflate extension (v1 no_context_takeover-only mode).
    /// </summary>
    public sealed class PerMessageDeflateExtension : IWebSocketExtension
    {
        private static readonly byte[] DeflateTail = new byte[] { 0x00, 0x00, 0xFF, 0xFF };

        private readonly PerMessageDeflateOptions _options;
        private readonly int _maxMessageSize;

        private bool _disposed;
        private bool _isNegotiated;

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

            if (_options.ServerNoContextTakeover)
                parameters["server_no_context_takeover"] = null;

            if (_options.ClientNoContextTakeover)
                parameters["client_no_context_takeover"] = null;

            parameters["server_max_window_bits"] = _options.ServerMaxWindowBits.ToString();
            parameters["client_max_window_bits"] = _options.ClientMaxWindowBits.ToString();

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
            int serverMaxWindowBits = _options.ServerMaxWindowBits;
            int clientMaxWindowBits = _options.ClientMaxWindowBits;

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

                        break;

                    case "server_max_window_bits":
                        if (!TryParseWindowBits(parameter.Value, out serverMaxWindowBits))
                            return false;
                        break;

                    case "client_max_window_bits":
                        if (!TryParseWindowBits(parameter.Value, out clientMaxWindowBits))
                            return false;
                        break;

                    default:
                        return false;
                }
            }

            // v1 requires no context takeover for inbound decompression safety.
            if (_options.ServerNoContextTakeover && !serverNoContextTakeover)
                return false;

            if (serverMaxWindowBits > _options.ServerMaxWindowBits)
                return false;

            if (clientMaxWindowBits > _options.ClientMaxWindowBits)
                return false;

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

            try
            {
                var compressedOwner = Compress(payload);
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

        private IMemoryOwner<byte> Compress(ReadOnlyMemory<byte> payload)
        {
            using var output = new MemoryStream(payload.Length + 32);

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

                deflate.Flush();
            }

            if (!output.TryGetBuffer(out ArraySegment<byte> compressedSegment) ||
                compressedSegment.Array == null)
            {
                byte[] compressedFallback = output.ToArray();
                int fallbackLength = compressedFallback.Length;
                if (HasTail(compressedFallback, fallbackLength, DeflateTail))
                    fallbackLength -= DeflateTail.Length;

                var fallbackOwner = ArrayPoolMemoryOwner<byte>.Rent(fallbackLength);
                if (fallbackLength > 0)
                {
                    new ReadOnlyMemory<byte>(compressedFallback, 0, fallbackLength).CopyTo(fallbackOwner.Memory);
                }

                return fallbackOwner;
            }

            int compressedLength = (int)output.Length;
            byte[] compressed = compressedSegment.Array;

            if (HasTail(compressed, compressedLength, DeflateTail))
                compressedLength -= DeflateTail.Length;

            var owner = ArrayPoolMemoryOwner<byte>.Rent(compressedLength);
            if (compressedLength > 0)
            {
                new ReadOnlyMemory<byte>(compressed, compressedSegment.Offset, compressedLength).CopyTo(owner.Memory);
            }

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
