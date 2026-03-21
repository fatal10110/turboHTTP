using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
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

        private CompressionKind[] _compressionChain;

        private enum CompressionKind
        {
            Gzip,
            Deflate
        }

        internal DecompressionHandler(IHttpHandler inner, long maxDecompressedBodySizeBytes)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            if (maxDecompressedBodySizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxDecompressedBodySizeBytes),
                    maxDecompressedBodySizeBytes,
                    "Must be > 0.");
            }

            _maxDecompressedBodySizeBytes = maxDecompressedBodySizeBytes;
            _maxCompressedBodySizeBytes = maxDecompressedBodySizeBytes;
            _compressionChain = Array.Empty<CompressionKind>();
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            if (body == null || !TryResolveCompression(headers, out _compressionChain))
            {
                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
                return;
            }

            if (body.Length.HasValue && body.Length.Value > _maxCompressedBodySizeBytes)
            {
                body.Abort();
                _inner.OnResponseError(
                    CreateDecompressionError(
                        new IOException(
                            $"Compressed response body exceeded the maximum size ({_maxCompressedBodySizeBytes} bytes).")),
                    context);
                return;
            }

            var forwardHeaders = headers?.Clone() ?? new HttpHeaders();
            forwardHeaders.Remove("Content-Encoding");
            forwardHeaders.Remove("Content-Length");

            if (body.TryGetBufferedData(out var compressed))
            {
                BufferedResponseBodySource bufferedBody = null;
                try
                {
                    var trailers = await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                    var decompressed = DecompressBufferedBody(
                        compressed,
                        _compressionChain,
                        _maxDecompressedBodySizeBytes);
                    bufferedBody = new BufferedResponseBodySource(decompressed, trailers);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                {
                    body.Abort();
                    _inner.OnResponseError(CreateDecompressionError(ex), context);
                    return;
                }
                finally
                {
                    await body.DisposeAsync().ConfigureAwait(false);
                }

                try
                {
                    await _inner.OnResponseStartAsync(statusCode, forwardHeaders, bufferedBody, context)
                        .ConfigureAwait(false);
                    bufferedBody = null;
                }
                catch
                {
                    if (bufferedBody != null)
                        await bufferedBody.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                return;
            }

            var decompressedBody = new DecompressionBodySource(
                body,
                _compressionChain,
                _maxDecompressedBodySizeBytes);

            try
            {
                await _inner.OnResponseStartAsync(
                        statusCode,
                        forwardHeaders,
                        decompressedBody,
                        context)
                    .ConfigureAwait(false);
                decompressedBody = null;
            }
            catch
            {
                decompressedBody?.Abort();
                throw;
            }
        }

        public void OnResponseError(UHttpException error, RequestContext context)
        {
            _inner.OnResponseError(error, context);
        }

        private static byte[] DecompressBufferedBody(
            ReadOnlyMemory<byte> compressed,
            CompressionKind[] compressionChain,
            long maxDecompressedBodySizeBytes)
        {
            var compressedSequence = new ReadOnlySequence<byte>(compressed);
            var validateSingleGzipTrailer =
                compressionChain.Length == 1 &&
                compressionChain[0] == CompressionKind.Gzip;

            using var compressedStream = new ReadOnlySequenceStream(compressedSequence);
            using var decompressionStream = CreateDecompressionStream(compressedStream, compressionChain);
            using var output = new MemoryStream();

            var buffer = new byte[64 * 1024];
            long totalDecompressed = 0;
            uint crc32 = uint.MaxValue;
            while (true)
            {
                var read = decompressionStream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    break;

                totalDecompressed += read;
                if (totalDecompressed > maxDecompressedBodySizeBytes)
                {
                    throw new IOException(
                        $"Response decompression exceeded the maximum size ({maxDecompressedBodySizeBytes} bytes).");
                }

                if (validateSingleGzipTrailer)
                    crc32 = UpdateCrc32(crc32, new ReadOnlySpan<byte>(buffer, 0, read));

                output.Write(buffer, 0, read);
            }

            if (validateSingleGzipTrailer)
                ValidateSingleGzipTrailer(compressedSequence, totalDecompressed, crc32 ^ uint.MaxValue);

            return output.ToArray();
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

        private sealed class DecompressionBodySource : IResponseBodySource
        {
            private const int ReadAheadBufferBytes = 8 * 1024;
            private const int DrainBufferBytes = 16 * 1024;

            private readonly IResponseBodySource _inner;
            private readonly long _maxDecompressedBodySizeBytes;
            private readonly BodySourceStream _compressedStream;
            private readonly BufferedStream _readAheadStream;
            private readonly Stream _decompressionStream;
            private readonly byte[] _overflowProbe = new byte[1];

            private long _bytesRead;
            private int _completed;
            private int _disposeStarted;
            private int _disposed;

            internal DecompressionBodySource(
                IResponseBodySource inner,
                CompressionKind[] compressionChain,
                long maxDecompressedBodySizeBytes)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                if (compressionChain == null)
                    throw new ArgumentNullException(nameof(compressionChain));
                if (maxDecompressedBodySizeBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(maxDecompressedBodySizeBytes),
                        maxDecompressedBodySizeBytes,
                        "Must be > 0.");
                }

                _maxDecompressedBodySizeBytes = maxDecompressedBodySizeBytes;
                _compressedStream = new BodySourceStream(inner);
                _readAheadStream = new BufferedStream(_compressedStream, ReadAheadBufferBytes);
                _decompressionStream = CreateDecompressionStream(_readAheadStream, compressionChain);
            }

            public long? Length
            {
                get
                {
                    ThrowIfDisposed();
                    return null;
                }
            }

            public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
            {
                ThrowIfDisposed();
                data = default;
                return false;
            }

            public bool TryDetachBufferedBody(out DetachedBufferedBody body)
            {
                ThrowIfDisposed();
                body = default;
                return false;
            }

            public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();

                if (destination.IsEmpty)
                    return 0;

                if (Volatile.Read(ref _completed) != 0)
                    return 0;

                try
                {
                    var remainingBudget = _maxDecompressedBodySizeBytes - Interlocked.Read(ref _bytesRead);
                    if (remainingBudget <= 0)
                        return await ProbePastLimitAsync(ct).ConfigureAwait(false);

                    var boundedDestination = destination.Slice(
                        0,
                        (int)Math.Min(destination.Length, remainingBudget));

                    var read = await _decompressionStream.ReadAsync(boundedDestination, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        Volatile.Write(ref _completed, 1);
                        return 0;
                    }

                    var totalRead = Interlocked.Add(ref _bytesRead, read);
                    if (totalRead > _maxDecompressedBodySizeBytes)
                        ThrowLimitExceeded();

                    return read;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (UHttpException)
                {
                    Abort();
                    throw;
                }
                catch (InvalidDataException ex)
                {
                    Abort();
                    throw CreateDecompressionError(ex);
                }
                catch (IOException ex)
                {
                    Abort();
                    throw CreateDecompressionError(ex);
                }
            }

            public async ValueTask DrainAsync(CancellationToken ct)
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();

                if (Volatile.Read(ref _completed) != 0)
                    return;

                var buffer = ArrayPool<byte>.Shared.Rent(DrainBufferBytes);
                try
                {
                    while (await ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false) != 0)
                    {
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            public void Abort()
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                    return;

                Interlocked.Exchange(ref _disposed, 1);
                DisposeStreams();
                _inner.Abort();
            }

            public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
            {
                ThrowIfDisposed();
                ct.ThrowIfCancellationRequested();

                if (Volatile.Read(ref _completed) == 0)
                    await DrainAsync(ct).ConfigureAwait(false);

                return await _inner.GetTrailersAsync(ct).ConfigureAwait(false);
            }

            public async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
                    return;

                var drained = false;
                try
                {
                    if (Volatile.Read(ref _completed) == 0)
                    {
                        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    drained = true;
                }
                catch
                {
                    _inner.Abort();
                }
                finally
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    DisposeStreams();
                }

                if (drained)
                {
                    try
                    {
                        await _inner.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        _inner.Abort();
                    }
                }
            }

            private async ValueTask<int> ProbePastLimitAsync(CancellationToken ct)
            {
                var read = await _decompressionStream.ReadAsync(_overflowProbe.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    Volatile.Write(ref _completed, 1);
                    return 0;
                }

                ThrowLimitExceeded();
                return 0;
            }

            private void ThrowLimitExceeded()
            {
                Abort();
                throw CreateDecompressionError(
                    new IOException(
                        $"Response decompression exceeded the maximum size ({_maxDecompressedBodySizeBytes} bytes)."));
            }

            private void DisposeStreams()
            {
                try
                {
                    _decompressionStream.Dispose();
                }
                catch
                {
                }

                try
                {
                    _readAheadStream.Dispose();
                }
                catch
                {
                }

                try
                {
                    _compressedStream.Dispose();
                }
                catch
                {
                }
            }

            private void ThrowIfDisposed()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(DecompressionBodySource));
            }

            private sealed class BodySourceStream : Stream
            {
                private readonly IResponseBodySource _inner;
                private int _disposed;

                internal BodySourceStream(IResponseBodySource inner)
                {
                    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                }

                public override bool CanRead => Volatile.Read(ref _disposed) == 0;

                public override bool CanSeek => false;

                public override bool CanWrite => false;

                public override long Length => throw new NotSupportedException();

                public override long Position
                {
                    get => throw new NotSupportedException();
                    set => throw new NotSupportedException();
                }

                public override void Flush()
                {
                }

                public override Task FlushAsync(CancellationToken cancellationToken)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }

                public override int Read(byte[] buffer, int offset, int count)
                {
                    ValidateReadArguments(buffer, offset, count);
                    ThrowIfDisposed();
                    return _inner.ReadAsync(
                            new Memory<byte>(buffer, offset, count),
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }

                public override int Read(Span<byte> buffer)
                {
                    ThrowIfDisposed();
                    if (buffer.IsEmpty)
                        return 0;

                    var rented = ArrayPool<byte>.Shared.Rent(buffer.Length);
                    try
                    {
                        var read = _inner.ReadAsync(
                                rented.AsMemory(0, buffer.Length),
                                CancellationToken.None)
                            .GetAwaiter()
                            .GetResult();
                        if (read > 0)
                            rented.AsSpan(0, read).CopyTo(buffer);

                        return read;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }

                public override Task<int> ReadAsync(
                    byte[] buffer,
                    int offset,
                    int count,
                    CancellationToken cancellationToken)
                {
                    ValidateReadArguments(buffer, offset, count);
                    ThrowIfDisposed();
                    return _inner.ReadAsync(
                            new Memory<byte>(buffer, offset, count),
                            cancellationToken)
                        .AsTask();
                }

                public override ValueTask<int> ReadAsync(
                    Memory<byte> buffer,
                    CancellationToken cancellationToken = default)
                {
                    ThrowIfDisposed();
                    return _inner.ReadAsync(buffer, cancellationToken);
                }

                public override long Seek(long offset, SeekOrigin origin)
                {
                    throw new NotSupportedException();
                }

                public override void SetLength(long value)
                {
                    throw new NotSupportedException();
                }

                public override void Write(byte[] buffer, int offset, int count)
                {
                    throw new NotSupportedException();
                }

                public override void Write(ReadOnlySpan<byte> buffer)
                {
                    throw new NotSupportedException();
                }

                public override Task WriteAsync(
                    byte[] buffer,
                    int offset,
                    int count,
                    CancellationToken cancellationToken)
                {
                    throw new NotSupportedException();
                }

                public override ValueTask WriteAsync(
                    ReadOnlyMemory<byte> buffer,
                    CancellationToken cancellationToken = default)
                {
                    throw new NotSupportedException();
                }

                protected override void Dispose(bool disposing)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    base.Dispose(disposing);
                }

                public override ValueTask DisposeAsync()
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    return default;
                }

                private void ThrowIfDisposed()
                {
                    if (Volatile.Read(ref _disposed) != 0)
                        throw new ObjectDisposedException(nameof(BodySourceStream));
                }

                private static void ValidateReadArguments(byte[] buffer, int offset, int count)
                {
                    if (buffer == null)
                        throw new ArgumentNullException(nameof(buffer));
                    if ((uint)offset > buffer.Length)
                        throw new ArgumentOutOfRangeException(nameof(offset));
                    if ((uint)count > buffer.Length - offset)
                        throw new ArgumentOutOfRangeException(nameof(count));
                }
            }
        }

        private static Stream CreateDecompressionStream(
            Stream compressedStream,
            CompressionKind[] compressionChain)
        {
            Stream current = compressedStream;
            for (int i = compressionChain.Length - 1; i >= 0; i--)
            {
                current = CreateSingleDecompressionStream(current, compressionChain[i]);
            }

            return current;
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
            var start = 0;
            while (start < contentEncoding.Length)
            {
                var end = contentEncoding.IndexOf(',', start);
                if (end < 0)
                    end = contentEncoding.Length;

                var tokenStart = start;
                var tokenEnd = end;
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
            if (string.Equals(token, "gzip", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "x-gzip", StringComparison.OrdinalIgnoreCase))
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
            for (var i = 0; i < data.Length; i++)
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
                for (var bit = 0; bit < 8; bit++)
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
