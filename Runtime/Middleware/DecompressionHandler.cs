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
        private readonly IHttpHandler _inner;
        private readonly long _maxDecompressedBodySizeBytes;

        private SegmentedBuffer _compressedBuffer;
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
                _inner.OnResponseStart(statusCode, headers, context);
                return;
            }

            _forwardHeaders = headers.Clone();
            _forwardHeaders.Remove("Content-Encoding");
            _forwardHeaders.Remove("Content-Length");
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
                _inner.OnResponseError(new UHttpException(
                    new UHttpError(UHttpErrorType.Unknown, "Response decompression failed.", ex)), context);
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

            using var compressedStream = new ReadOnlySequenceStream(_compressedBuffer.AsSequence());
            using var decompressionStream = CreateDecompressionStream(compressedStream);

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long totalDecompressed = 0;
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

                    _inner.OnResponseData(new ReadOnlySpan<byte>(buffer, 0, read), context);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
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
                    return new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                case CompressionKind.Deflate:
                    return new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                default:
                    throw new InvalidOperationException("Compression mode is not active.");
            }
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
    }
}
