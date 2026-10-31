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

        // Written by OnRequestStart, read by OnResponseStartAsync of the same dispatch.
        private int _isHeadRequest;

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
        }

        public void OnRequestStart(UHttpRequest request, RequestContext context)
        {
            Volatile.Write(ref _isHeadRequest, request != null && request.Method == HttpMethod.HEAD ? 1 : 0);
            _inner.OnRequestStart(request, context);
        }

        public async ValueTask OnResponseStartAsync(
            int statusCode,
            HttpHeaders headers,
            IResponseBodySource body,
            RequestContext context)
        {
            CompressionKind[] compressionChain;
            if (body == null ||
                HasNoContent(statusCode, body, context) ||
                !TryResolveCompression(headers, out compressionChain))
            {
                // RFC 9110 sections 6.4.1, 9.3.2, 15.4.5: HEAD responses and 1xx/204/304 carry no content, so
                // Content-Encoding describes the selected representation, not bytes on the wire. Forward the
                // response and its headers untouched instead of failing to decode an empty body.
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
                        compressionChain,
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
                compressionChain,
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

        private bool HasNoContent(int statusCode, IResponseBodySource body, RequestContext context)
        {
            if (statusCode < 200 || statusCode == 204 || statusCode == 304)
                return true;

            if (Volatile.Read(ref _isHeadRequest) != 0 ||
                (context?.Request != null && context.Request.Method == HttpMethod.HEAD))
            {
                return true;
            }

            if (body.Length.HasValue && body.Length.Value == 0)
                return true;

            return body.TryGetBufferedData(out var buffered) && buffered.IsEmpty;
        }

        private static byte[] DecompressBufferedBody(
            ReadOnlyMemory<byte> compressed,
            CompressionKind[] compressionChain,
            long maxDecompressedBodySizeBytes)
        {
            var compressedSequence = new ReadOnlySequence<byte>(compressed);

            // Every gzip layer validates its CRC32/ISIZE trailer at end of stream (DecoderCompletionStream).
            using var compressedStream = new ReadOnlySequenceStream(compressedSequence);
            using var decompressionStream = CreateDecompressionStream(compressedStream, compressionChain);
            using var output = new MemoryStream();

            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                long totalDecompressed = 0;
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

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return output.ToArray();
        }

        private sealed class DecompressionBodySource : IResponseBodySource
        {
            private const int ReadAheadBufferBytes = 8 * 1024;
            private const int DrainBufferBytes = 16 * 1024;
            private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

            private readonly IResponseBodySource _inner;
            private readonly long _maxDecompressedBodySizeBytes;
            private readonly BodySourceStream _compressedStream;
            private readonly BufferedStream _readAheadStream;
            private readonly Stream _decompressionStream;

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

                    var read = await ReadFromDecompressionStreamAsync(boundedDestination, ct).ConfigureAwait(false);
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
                CancellationTokenSource drainTimeoutCts = null;
                try
                {
                    if (Volatile.Read(ref _completed) == 0)
                    {
                        drainTimeoutCts = new CancellationTokenSource(DisposeDrainTimeout);
                        await DrainAsync(drainTimeoutCts.Token).ConfigureAwait(false);
                    }

                    drained = true;
                }
                catch
                {
                    // Abort is the terminal transport-side release primitive for failed drain paths.
                    // The response-body source contract requires Abort() to tear down remaining
                    // transport state when DisposeAsync cannot complete a clean drain.
                    _inner.Abort();
                }
                finally
                {
                    drainTimeoutCts?.Dispose();
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
                var overflowProbe = new byte[1];
                var read = await ReadFromDecompressionStreamAsync(overflowProbe, ct).ConfigureAwait(false);
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

            private ValueTask<int> ReadFromDecompressionStreamAsync(
                Memory<byte> destination,
                CancellationToken ct)
            {
                if (SynchronizationContext.Current == null)
                    return _decompressionStream.ReadAsync(destination, ct);

                // Unity's compression streams may route ReadAsync through the synchronous
                // Stream.Read path on some runtimes. Offload that path so async callers on a
                // pumping thread do not block while the inner body source waits on network I/O.
                return new ValueTask<int>(Task.Run(
                    async () => await _decompressionStream.ReadAsync(destination, ct).ConfigureAwait(false),
                    ct));
            }

            private void DisposeStreams()
            {
                // Disposal runs after the body outcome is already decided: a truncation or trailer failure was
                // thrown from ReadAsync (and surfaced to the consumer) before Abort() reaches this point, so a
                // dispose-time failure cannot mask it. Record it instead of swallowing it silently (#180 will
                // route this to the diagnostic sink).
                try
                {
                    _decompressionStream.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TurboHTTP][Decompression] Decompression stream dispose failed: " + ex.Message);
                }

                try
                {
                    _readAheadStream.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TurboHTTP][Decompression] Read-ahead stream dispose failed: " + ex.Message);
                }

                try
                {
                    _compressedStream.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[TurboHTTP][Decompression] Compressed stream dispose failed: " + ex.Message);
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
                    System.Diagnostics.Debug.Assert(
                        SynchronizationContext.Current == null,
                        "BodySourceStream.Read should not block a pumping SynchronizationContext.");
                    // GZipStream/DeflateStream still call the synchronous Stream.Read path internally
                    // on some runtimes. DecompressionBodySource.ReadAsync offloads those calls when it
                    // detects a SynchronizationContext, so this sync bridge only blocks worker-thread
                    // consumers or explicit synchronous callers.
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

                    System.Diagnostics.Debug.Assert(
                        SynchronizationContext.Current == null,
                        "BodySourceStream.Read should not block a pumping SynchronizationContext.");
                    // Same invariant as Read(byte[],...): async callers with a SynchronizationContext
                    // are offloaded before they reach this synchronous bridge.
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

        /// <summary>
        /// Pass-through read stream under a <see cref="GZipStream"/> that counts the compressed bytes the
        /// decoder (and the post-EOF drain) pulled and keeps the last 8 of them: the RFC 1952 member trailer.
        /// Only one reader uses it at a time (the decoder, then the validating stream after decoder EOF), so the
        /// fields need no synchronization; async continuations order the accesses.
        /// </summary>
        private sealed class GzipInputTrackingStream : Stream
        {
            internal const int TrailerBytes = 8;

            private readonly Stream _inner;
            private readonly bool _leaveOpen;
            private readonly byte[] _tail = new byte[TrailerBytes];
            private int _tailCount;
            private long _totalBytes;
            private int _disposed;

            internal GzipInputTrackingStream(Stream inner, bool leaveOpen)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _leaveOpen = leaveOpen;
            }

            internal long TotalBytes => _totalBytes;

            public override bool CanRead => Volatile.Read(ref _disposed) == 0;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            internal bool TryCopyTrailer(Span<byte> destination)
            {
                if (_tailCount < TrailerBytes || destination.Length < TrailerBytes)
                    return false;

                _tail.AsSpan().CopyTo(destination);
                return true;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _inner.Read(buffer, offset, count);
                Record(new ReadOnlySpan<byte>(buffer, offset, read));
                return read;
            }

            public override int Read(Span<byte> buffer)
            {
                var read = _inner.Read(buffer);
                Record(buffer.Slice(0, read));
                return read;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var pending = _inner.ReadAsync(buffer, cancellationToken);
                if (pending.IsCompletedSuccessfully)
                {
                    var read = pending.Result;
                    Record(buffer.Span.Slice(0, read));
                    return new ValueTask<int>(read);
                }

                return AwaitReadAsync(pending, buffer);
            }

            private async ValueTask<int> AwaitReadAsync(ValueTask<int> pending, Memory<byte> buffer)
            {
                var read = await pending.ConfigureAwait(false);
                Record(buffer.Span.Slice(0, read));
                return read;
            }

            private void Record(ReadOnlySpan<byte> data)
            {
                if (data.IsEmpty)
                    return;

                _totalBytes += data.Length;
                if (data.Length >= TrailerBytes)
                {
                    data.Slice(data.Length - TrailerBytes).CopyTo(_tail);
                    _tailCount = TrailerBytes;
                    return;
                }

                // Keep the newest (8 - data.Length) bytes already held, then append data.
                var keep = Math.Min(_tailCount, TrailerBytes - data.Length);
                _tail.AsSpan(_tailCount - keep, keep).CopyTo(_tail);
                data.CopyTo(_tail.AsSpan(keep));
                _tailCount = keep + data.Length;
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen)
                    _inner.Dispose();

                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// Wraps one decoder of the chain. When the decoder reports end of stream it drains the rest of its
        /// input (bounded): a decoder stops reading once its own format ends, so without the drain the layer
        /// below (for example the gzip layer under a deflate layer) would never reach its own end and check.
        /// For a gzip layer it also CRC32s the decoded bytes and checks the RFC 1952 trailer against the CRC32
        /// and the decoded size mod 2^32: a body cut inside the deflate data or inside the trailer fails with
        /// <see cref="InvalidDataException"/> instead of reading as a clean end of stream. Disposing before end
        /// of stream skips the check by design (early dispose is not a completed body).
        /// </summary>
        private sealed class DecoderCompletionStream : Stream
        {
            private const int MinimumGzipMemberBytes = 18;
            private const int MaxBytesAfterMemberEnd = 64 * 1024;
            private const int DrainChunkBytes = 4 * 1024;

            private const int StateReading = 0;
            private const int StateValidated = 1;
            private const int StateFailed = 2;

            private readonly Stream _decoder;
            private readonly Stream _drainSource;
            private readonly GzipInputTrackingStream _gzipInput;
            private uint _crc32 = uint.MaxValue;
            private long _decodedBytes;
            private int _state;
            private int _disposed;

            /// <param name="decoder">The decoder this stream reads from; disposed with this stream.</param>
            /// <param name="drainSource">The decoder's input, drained to its end after the decoder's EOF.</param>
            /// <param name="gzipInput">For a gzip layer, the tracked compressed input holding the trailer; else null.</param>
            internal DecoderCompletionStream(Stream decoder, Stream drainSource, GzipInputTrackingStream gzipInput)
            {
                _decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
                _drainSource = drainSource ?? throw new ArgumentNullException(nameof(drainSource));
                _gzipInput = gzipInput;
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

            public override int Read(byte[] buffer, int offset, int count)
            {
                return Read(new Span<byte>(buffer, offset, count));
            }

            public override int Read(Span<byte> buffer)
            {
                if (buffer.IsEmpty)
                    return 0;
                if (!CheckReadable())
                    return 0;

                var read = _decoder.Read(buffer);
                if (read > 0)
                {
                    Observe(buffer.Slice(0, read));
                    return read;
                }

                var scratch = ArrayPool<byte>.Shared.Rent(DrainChunkBytes);
                try
                {
                    long drained = 0;
                    int n;
                    while ((n = _drainSource.Read(scratch, 0, DrainChunkBytes)) > 0)
                        drained = CountDrained(drained, n);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }

                ValidateTrailer();
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (buffer.IsEmpty)
                    return new ValueTask<int>(0);
                if (!CheckReadable())
                    return new ValueTask<int>(0);

                var pending = _decoder.ReadAsync(buffer, cancellationToken);
                if (pending.IsCompletedSuccessfully)
                {
                    // Consume the ValueTask exactly once.
                    var read = pending.Result;
                    if (read > 0)
                    {
                        Observe(buffer.Span.Slice(0, read));
                        return new ValueTask<int>(read);
                    }

                    return CompleteReadAsync(new ValueTask<int>(0), buffer, cancellationToken);
                }

                return CompleteReadAsync(pending, buffer, cancellationToken);
            }

            private async ValueTask<int> CompleteReadAsync(
                ValueTask<int> pending,
                Memory<byte> buffer,
                CancellationToken cancellationToken)
            {
                var read = await pending.ConfigureAwait(false);
                if (read > 0)
                {
                    Observe(buffer.Span.Slice(0, read));
                    return read;
                }

                var scratch = ArrayPool<byte>.Shared.Rent(DrainChunkBytes);
                try
                {
                    long drained = 0;
                    int n;
                    while ((n = await _drainSource.ReadAsync(scratch.AsMemory(0, DrainChunkBytes), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        drained = CountDrained(drained, n);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(scratch);
                }

                ValidateTrailer();
                return 0;
            }

            private bool CheckReadable()
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new ObjectDisposedException(nameof(DecoderCompletionStream));

                var state = Volatile.Read(ref _state);
                if (state == StateFailed)
                    throw new InvalidDataException("GZIP trailer validation failed.");

                return state == StateReading;
            }

            private void Observe(ReadOnlySpan<byte> decoded)
            {
                if (_gzipInput == null)
                    return;

                _crc32 = UpdateCrc32(_crc32, decoded);
                _decodedBytes += decoded.Length;
            }

            private long CountDrained(long drained, int read)
            {
                drained += read;
                if (drained > MaxBytesAfterMemberEnd)
                {
                    Volatile.Write(ref _state, StateFailed);
                    throw new InvalidDataException("Unexpected data after the end of the compressed payload.");
                }

                return drained;
            }

            private void ValidateTrailer()
            {
                if (_gzipInput == null)
                {
                    Volatile.Write(ref _state, StateValidated);
                    return;
                }

                Span<byte> trailer = stackalloc byte[GzipInputTrackingStream.TrailerBytes];
                if (_gzipInput.TotalBytes < MinimumGzipMemberBytes || !_gzipInput.TryCopyTrailer(trailer))
                {
                    Volatile.Write(ref _state, StateFailed);
                    throw new InvalidDataException("GZIP payload is truncated.");
                }

                var expectedCrc32 = ReadUInt32LittleEndian(trailer);
                var expectedSize = ReadUInt32LittleEndian(trailer.Slice(4));
                if (expectedCrc32 != (_crc32 ^ uint.MaxValue) || expectedSize != unchecked((uint)_decodedBytes))
                {
                    Volatile.Write(ref _state, StateFailed);
                    throw new InvalidDataException("GZIP trailer validation failed.");
                }

                Volatile.Write(ref _state, StateValidated);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                    _decoder.Dispose();

                base.Dispose(disposing);
            }
        }

        private static Stream CreateDecompressionStream(
            Stream compressedStream,
            CompressionKind[] compressionChain)
        {
            // Content-Encoding lists codings in the order they were applied (RFC 9110 section 8.4), so the last
            // one wraps the wire bytes. The caller owns compressedStream; every decoder created here owns the
            // decoder below it, so disposing the returned stream releases the whole chain.
            Stream current = compressedStream;
            var leaveOpen = true;
            for (int i = compressionChain.Length - 1; i >= 0; i--)
            {
                current = CreateSingleDecompressionStream(current, compressionChain[i], leaveOpen);
                leaveOpen = false;
            }

            return current;
        }

        private static Stream CreateSingleDecompressionStream(
            Stream compressedStream,
            CompressionKind compression,
            bool leaveOpen)
        {
            switch (compression)
            {
                case CompressionKind.Gzip:
                    {
                        // Unity's Mono GZipStream can report a truncated member as a clean end of stream, so each
                        // gzip layer checks the RFC 1952 trailer (CRC32 + ISIZE) itself and fails on a mismatch or
                        // a premature EOF.
                        var input = new GzipInputTrackingStream(compressedStream, leaveOpen);
                        var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);
                        return new DecoderCompletionStream(gzip, input, input);
                    }
                case CompressionKind.Deflate:
                    {
                        var deflate = new DeflateStream(compressedStream, CompressionMode.Decompress, leaveOpen);

                        // Raw deflate has no trailer. Stacked on another decoder, drain that decoder to its end so
                        // a gzip layer below still validates; on the wire bytes (leaveOpen) no wrapper is needed.
                        return leaveOpen ? (Stream)deflate : new DecoderCompletionStream(deflate, compressedStream, null);
                    }
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
