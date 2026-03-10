using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Middleware
{
    internal sealed class DecompressionHandler : IHttpHandler
    {
        private readonly IHttpHandler _inner;

        private SegmentedBuffer _compressedBuffer;
        private CompressionKind _compression;
        private HttpHeaders _forwardHeaders;

        private enum CompressionKind
        {
            None,
            Gzip,
            Deflate
        }

        internal DecompressionHandler(IHttpHandler inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public void OnResponseStart(int statusCode, HttpHeaders headers, RequestContext context)
        {
            _compression = ResolveCompression(headers);
            if (_compression == CompressionKind.None)
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
            if (_compression == CompressionKind.None)
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
            if (_compression == CompressionKind.None)
            {
                _inner.OnResponseEnd(trailers, context);
                return;
            }

            try
            {
                DecompressBufferedBody(context);
            }
            catch (InvalidDataException ex)
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
            try
            {
                while (true)
                {
                    var read = decompressionStream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;

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
            switch (_compression)
            {
                case CompressionKind.Gzip:
                    return new GZipStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                case CompressionKind.Deflate:
                    return new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen: false);
                default:
                    throw new InvalidOperationException("Compression mode is not active.");
            }
        }

        private void DisposeCompressedBuffer()
        {
            _compressedBuffer?.Dispose();
            _compressedBuffer = null;
        }

        private static CompressionKind ResolveCompression(HttpHeaders headers)
        {
            var contentEncoding = headers.Get("Content-Encoding");
            if (string.IsNullOrWhiteSpace(contentEncoding))
                return CompressionKind.None;

            if (string.Equals(contentEncoding.Trim(), "gzip", StringComparison.OrdinalIgnoreCase))
                return CompressionKind.Gzip;

            if (string.Equals(contentEncoding.Trim(), "deflate", StringComparison.OrdinalIgnoreCase))
                return CompressionKind.Deflate;

            return CompressionKind.None;
        }
    }
}
