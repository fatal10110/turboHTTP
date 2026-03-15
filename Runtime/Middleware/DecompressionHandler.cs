using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Middleware
{
    internal sealed class DecompressionHandler : IHttpHandler
    {
        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private readonly IHttpHandler _inner;
        private readonly long _maxDecompressedBodySizeBytes;
        private readonly long _maxCompressedBodySizeBytes;

        private SegmentedBuffer _compressedBuffer;
        private long _compressedBytes;
        private CompressionKind[] _compressionChain;
        private HttpHeaders _forwardHeaders;

        private enum CompressionKind
        {
            Gzip,
            Deflate
        }

        internal DecompressionHandler(IHttpHandler inner, long maxDecompressedBodySizeBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maxDecompressedBodySizeBytes <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxDecompressedBodySizeBytes),
                    maxDecompressedBodySizeBytes,
                    "Must be > 0.");

            _maxDecompressedBodySizeBytes = maxDecompressedBodySizeBytes;
            _maxCompressedBodySizeBytes = maxDecompressedBodySizeBytes;
            _compressionChain = Array.Empty<CompressionKind>();
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            if (!TryResolveCompression(headers, out _compressionChain))
            {
                _forwardHeaders = headers;
                _compressedBytes = 0;
                _inner.OnResponseStart(statusCode, headers, context);
                return;
            }

            _forwardHeaders = headers.Clone();
            _forwardHeaders.Remove("Content-Encoding");
            _forwardHeaders.Remove("Content-Length");
            _compressedBytes = 0;
            _inner.OnResponseStart(statusCode, _forwardHeaders, context);
        }

        public void OnResponseData(ReadOnlySpan<byte> chunk, RequestContext context)
        {
            if (_compressionChain.Length == 0)
            {
                _inner.OnResponseData(chunk, context);
                return;
            }

            if (chunk.IsEmpty)
                return;

            if (_compressedBuffer == null)
                _compressedBuffer = new SegmentedBuffer();

            _compressedBytes += chunk.Length;
            if (_compressedBytes > _maxCompressedBodySizeBytes)
            {
                throw new IOException(
                    $"Compressed response body exceeded the maximum size ({_maxCompressedBodySizeBytes} bytes).");
            }

            _compressedBuffer.Write(chunk);
        }

        public void OnResponseEnd(HttpHeaders trailers, RequestContext context)
        {
            if (_compressionChain.Length == 0)
            {
                _inner.OnResponseEnd(trailers, context);
                return;
            }

            try
            {
                DecompressBufferedBody(context);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
            {
                _inner.OnResponseError(CreateDecompressionError(ex), context);
                return;
            }
            catch (Exception ex)
            {
                _inner.OnResponseError(WrapUnexpectedError(ex), context);
                return;
            }
            finally
            {
                DisposeCompressedBuffer();
            }

            _inner.OnResponseEnd(trailers, context);
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            DisposeCompressedBuffer();
            _inner.OnResponseError(error, context);
        }

        private void DecompressBufferedBody(RequestContext context)
        {
            if (_compressedBuffer == null)
                return;

            var compressedSequence = _compressedBuffer.AsSequence();
            var validateSingleGzipTrailer =
                _compressionChain.Length == 1 &&
                _compressionChain[0] == CompressionKind.Gzip;

            using var compressedStream = new ReadOnlySequenceStream(compressedSequence);
            using var decompressionStream = CreateDecompressionStream(compressedStream);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long totalDecompressed = 0;
            uint crc32 = uint.MaxValue;
            try
            {
                while (true)
                {
                    var read = decompressionStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

                    totalDecompressed += read;
                    if (totalDecompressed > _maxDecompressedBodySizeBytes)
                    {
                        throw new IOException(
                            $"Response decompression exceeded the maximum size ({_maxDecompressedBodySizeBytes} bytes).");
                    }

                    if (validateSingleGzipTrailer)
                        crc32 = UpdateCrc32(crc32, new ReadOnlySpan<byte>(buffer, 0, read));

                    _inner.OnResponseData(new ReadOnlySpan<byte>(buffer, 0, read), context);
                }

                if (validateSingleGzipTrailer)
                    ValidateSingleGzipTrailer(compressedSequence, totalDecompressed, crc32 ^ uint.MaxValue);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static void ValidateSingleGzipTrailer(
            ReadOnlySequence<byte> compressedSequence,
            long totalDecompressed,
            uint actualCrc32)
        {
            if (compressedSequence.Length < 18)
                throw new InvalidDataException("GZIP payload is truncated.");

            Span<byte> trailer = stackalloc byte[8];
            compressedSequence.Slice(compressedSequence.Length - trailer.Length).CopyTo(trailer);

            var expectedCrc32 = ReadUInt32LittleEndian(trailer);
            var expectedSize = ReadUInt32LittleEndian(trailer.Slice(4));
            if (expectedCrc32 != actualCrc32 || expectedSize != unchecked((uint)totalDecompressed))
            {
                throw new InvalidDataException("GZIP trailer validation failed.");
            }
        }

        private Stream CreateDecompressionStream(Stream compressedStream)
        {
            Stream current = compressedStream;
            for (int i = _compressionChain.Length - 1; i >= 0; i--)
            {
                current = CreateSingleDecompressionStream(current, _compressionChain[i]);
            }

            return current;
        }

        private void DisposeCompressedBuffer()
        {
            _compressedBuffer?.Dispose();
            _compressedBuffer = null;
        }

        private static Stream CreateSingleDecompressionStream(Stream compressedStream, CompressionKind compression)
        {
            switch (compression)
            {
                case CompressionKind.Gzip:
                    return new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                case CompressionKind.Deflate:
                    return new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: true);
                default:
                    throw new InvalidOperationException("Compression mode is not active.");
            }
        }

        private static UHttpException CreateDecompressionError(Exception ex)
        {
            return new UHttpException(
                new UHttpError(UHttpErrorType.Unknown, "Response decompression failed.", ex));
        }

        private static UHttpException WrapUnexpectedError(Exception ex)
        {
            if (ex is UHttpException uHttpException)
                return uHttpException;

            return new UHttpException(
                new UHttpError(UHttpErrorType.Unknown, ex?.Message ?? "Response decompression failed.", ex));
        }

        private static bool TryResolveCompression(HttpHeaders headers, out CompressionKind[] compressionChain)
        {
            if (headers == null)
            {
                compressionChain = Array.Empty<CompressionKind>();
                return false;
            }

            var contentEncoding = headers.Get("Content-Encoding");
            if (string.IsNullOrWhiteSpace(contentEncoding))
            {
                compressionChain = Array.Empty<CompressionKind>();
                return false;
            }

            var resolved = new List<CompressionKind>(4);
            int start = 0;
            while (start < contentEncoding.Length)
            {
                int end = contentEncoding.IndexOf(',', start);
                if (end < 0)
                    end = contentEncoding.Length;

                int tokenStart = start;
                int tokenEnd = end;
                while (tokenStart < tokenEnd && char.IsWhiteSpace(contentEncoding[tokenStart]))
                    tokenStart++;
                while (tokenEnd > tokenStart && char.IsWhiteSpace(contentEncoding[tokenEnd - 1]))
                    tokenEnd--;

                if (tokenEnd > tokenStart)
                {
                    if (!TryParseCompressionKind(
                            contentEncoding.Substring(tokenStart, tokenEnd - tokenStart),
                            out var compression))
                    {
                        compressionChain = Array.Empty<CompressionKind>();
                        return false;
                    }

                    if (compression.HasValue)
                        resolved.Add(compression.Value);
                }

                start = end + 1;
            }

            if (resolved.Count == 0)
            {
                compressionChain = Array.Empty<CompressionKind>();
                return false;
            }

            compressionChain = resolved.ToArray();
            return true;
        }

        private static bool TryParseCompressionKind(string token, out CompressionKind? compression)
        {
            if (string.Equals(token, "gzip", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "x-gzip", StringComparison.OrdinalIgnoreCase))
            {
                compression = CompressionKind.Gzip;
                return true;
            }

            if (string.Equals(token, "deflate", StringComparison.OrdinalIgnoreCase))
            {
                compression = CompressionKind.Deflate;
                return true;
            }

            if (string.Equals(token, "identity", StringComparison.OrdinalIgnoreCase))
            {
                compression = null;
                return true;
            }

            compression = null;
            return false;
        }

        private static uint UpdateCrc32(uint crc32, ReadOnlySpan<byte> data)
        {
            var crc = crc32;
            for (int i = 0; i < data.Length; i++)
                crc = Crc32Table[(int)((crc ^ data[i]) & 0xFF)] ^ (crc >> 8);

            return crc;
        }

        private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> bytes)
        {
            return (uint)(bytes[0]
                | (bytes[1] << 8)
                | (bytes[2] << 16)
                | (bytes[3] << 24));
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                var value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0
                        ? 0xEDB88320u ^ (value >> 1)
                        : value >> 1;
                }

                table[(int)i] = value;
            }

            return table;
        }
    }
}
