using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

namespace TurboHTTP.Middleware
{
    internal sealed class DecompressionHandler : IHttpHandler
    {
        /// <summary>
        /// Most content codings a response may stack (#783), counted over every <c>Content-Encoding</c> field line;
        /// <c>identity</c> does not count. Real servers apply one coding, rarely two; a longer chain only multiplies
        /// decoder state and work, so it fails before any decoder is created.
        /// </summary>
        internal const int MaxContentCodings = 3;

        private readonly IHttpHandler _inner;
        private readonly long _maxDecompressedBodySizeBytes;
        private readonly long _maxCompressedBodySizeBytes;
        private readonly long _maxIntermediateBytes;
        private readonly CancellationToken _cancellationToken;

        // Written by OnRequestStart, read by OnResponseStartAsync of the same dispatch.
        private int _isHeadRequest;

        private enum CompressionKind
        {
            Gzip,
            Deflate
        }

        private enum CompressionResolution
        {
            PassThrough,
            Decode,
            TooManyCodings
        }

        /// <param name="inner">The next handler.</param>
        /// <param name="maxDecompressedBodySizeBytes">Decoded-size limit; also caps the compressed input.</param>
        /// <param name="cancellationToken">
        /// The dispatch token, observed while a buffered body is decoded inside <see cref="OnResponseStartAsync"/>.
        /// </param>
        internal DecompressionHandler(
            IHttpHandler inner,
            long maxDecompressedBodySizeBytes,
            CancellationToken cancellationToken = default)
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
            // #774: the inner layers of a chain may together inflate MaxContentCodings times the decoded limit, room
            // for every inner layer of an allowed chain even when a coding grows its input (incompressible data).
            _maxIntermediateBytes = maxDecompressedBodySizeBytes > long.MaxValue / MaxContentCodings
                ? long.MaxValue
                : maxDecompressedBodySizeBytes * MaxContentCodings;
            _cancellationToken = cancellationToken;
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
            CompressionKind[] compressionChain = null;
            var resolution = CompressionResolution.PassThrough;
            if (body != null && !HasNoContent(statusCode, body, context))
                resolution = ResolveCompression(headers, out compressionChain);

            if (resolution == CompressionResolution.PassThrough)
            {
                // RFC 9110 sections 6.4.1, 9.3.2, 15.4.5: HEAD responses and 1xx/204/304 carry no content, so
                // Content-Encoding describes the selected representation, not bytes on the wire. Forward the
                // response and its headers untouched instead of failing to decode an empty body.
                await _inner.OnResponseStartAsync(statusCode, headers, body, context).ConfigureAwait(false);
                return;
            }

            if (resolution == CompressionResolution.TooManyCodings)
            {
                // #783: fail before any decoder (and its buffers) exists; the body is never read.
                body.Abort();
                _inner.OnResponseError(
                    CreateDecompressionError(
                        new InvalidDataException(
                            $"Content-Encoding lists more than {MaxContentCodings} content codings.")),
                    context);
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
                SegmentedResponseBodySource bufferedBody = null;
                try
                {
                    var trailers = await body.GetTrailersAsync(CancellationToken.None).ConfigureAwait(false);
                    var decompressed = DecompressBufferedBody(
                        compressed,
                        compressionChain,
                        _maxDecompressedBodySizeBytes,
                        CreateBudget(_cancellationToken));
                    try
                    {
                        bufferedBody = new SegmentedResponseBodySource(decompressed, trailers);
                    }
                    catch
                    {
                        decompressed.Dispose();
                        throw;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is IOException)
                {
                    body.Abort();
                    _inner.OnResponseError(CreateDecompressionError(ex), context);
                    return;
                }
                finally
                {
                    try
                    {
                        await body.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The decoded segments are not handed on: return them before the failure propagates.
                        bufferedBody?.Abort();
                        throw;
                    }
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
                _maxDecompressedBodySizeBytes,
                CreateBudget(CancellationToken.None));

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

        private DecompressionBudget CreateBudget(CancellationToken cancellationToken)
        {
            return new DecompressionBudget(_maxCompressedBodySizeBytes, _maxIntermediateBytes, cancellationToken);
        }

        /// <summary>
        /// Decodes a buffered body into pooled 16 KB segments (#74): no growing contiguous buffer and no final
        /// copy, so the peak is the decoded size plus one segment. The limit is checked before a segment is
        /// added; at the limit a one-byte probe tells a complete body from one that would exceed it. The caller
        /// owns the returned buffer; on failure its segments are returned here. The budget bounds the input and
        /// intermediate work of every layer and carries the dispatch token, checked before every source read
        /// and every output step (#774).
        /// </summary>
        private static SegmentedBuffer DecompressBufferedBody(
            ReadOnlyMemory<byte> compressed,
            CompressionKind[] compressionChain,
            long maxDecompressedBodySizeBytes,
            DecompressionBudget budget)
        {
            var output = new SegmentedBuffer();
            try
            {
                // Every gzip/zlib layer validates its trailers and reads its input to the end (InflateLayerStream).
                using var compressedStream = new ReadOnlySequenceStream(new ReadOnlySequence<byte>(compressed));
                using var decompressionStream = CreateDecompressionStream(compressedStream, compressionChain, budget);

                Span<byte> probe = stackalloc byte[1];
                while (true)
                {
                    budget.ThrowIfCancellationRequested();
                    var remainingBudget = maxDecompressedBodySizeBytes - output.Length;
                    if (remainingBudget <= 0)
                    {
                        if (decompressionStream.Read(probe) > 0)
                        {
                            throw new IOException(
                                $"Response decompression exceeded the maximum size ({maxDecompressedBodySizeBytes} bytes).");
                        }

                        break;
                    }

                    var span = output.GetWriteSpan();
                    if (span.Length > remainingBudget)
                        span = span.Slice(0, (int)remainingBudget);

                    var read = decompressionStream.Read(span);
                    if (read <= 0)
                        break;

                    output.Advance(read);
                }

                return output;
            }
            catch
            {
                output.Dispose();
                throw;
            }
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
            private readonly DecompressionBudget _budget;

            private long _bytesRead;
            private int _completed;
            private int _disposeStarted;
            private int _disposed;

            internal DecompressionBodySource(
                IResponseBodySource inner,
                CompressionKind[] compressionChain,
                long maxDecompressedBodySizeBytes,
                DecompressionBudget budget)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _budget = budget ?? throw new ArgumentNullException(nameof(budget));
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
                _compressedStream = new BodySourceStream(inner, budget);
                _readAheadStream = new BufferedStream(_compressedStream, ReadAheadBufferBytes);
                _decompressionStream = CreateDecompressionStream(_readAheadStream, compressionChain, budget);
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

                // Synchronous source reads inside the chain have no token argument; they observe this read's (#774).
                _budget.CancellationToken = ct;
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

                var buffer = ByteArrayPool.Rent(DrainBufferBytes);
                try
                {
                    while (await ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false) != 0)
                    {
                    }
                }
                finally
                {
                    ByteArrayPool.Return(buffer);
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
                // dispose-time failure cannot mask it. Record it in the diagnostic sink instead of swallowing it
                // silently (#180).
                try
                {
                    _decompressionStream.Dispose();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP][Decompression] Decompression stream dispose failed", ex);
                }

                try
                {
                    _readAheadStream.Dispose();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP][Decompression] Read-ahead stream dispose failed", ex);
                }

                try
                {
                    _compressedStream.Dispose();
                }
                catch (Exception ex)
                {
                    TurboDiagnostics.Log("[TurboHTTP][Decompression] Compressed stream dispose failed", ex);
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
                private readonly DecompressionBudget _budget;
                private int _disposed;

                internal BodySourceStream(IResponseBodySource inner, DecompressionBudget budget)
                {
                    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                    _budget = budget ?? throw new ArgumentNullException(nameof(budget));
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
                    // consumers or explicit synchronous callers. The wait observes the token of the read in
                    // progress (#774).
                    return _inner.ReadAsync(
                            new Memory<byte>(buffer, offset, count),
                            _budget.CancellationToken)
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
                    var rented = ByteArrayPool.Rent(buffer.Length);
                    try
                    {
                        var read = _inner.ReadAsync(
                                rented.AsMemory(0, buffer.Length),
                                _budget.CancellationToken)
                            .GetAwaiter()
                            .GetResult();
                        if (read > 0)
                            rented.AsSpan(0, read).CopyTo(buffer);

                        return read;
                    }
                    finally
                    {
                        ByteArrayPool.Return(rented);
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
            CompressionKind[] compressionChain,
            DecompressionBudget budget)
        {
            // Content-Encoding lists codings in the order they were applied (RFC 9110 section 8.4), so the last
            // one wraps the wire bytes. The caller owns compressedStream; every decoder created here owns the
            // decoder below it, so disposing the returned stream releases the whole chain. The first decoder reads
            // the wire bytes; every later one reads intermediate output, both charged to the shared budget.
            Stream current = compressedStream;
            var wire = true;
            for (int i = compressionChain.Length - 1; i >= 0; i--)
            {
                current = CreateSingleDecompressionStream(current, compressionChain[i], wire, budget);
                wire = false;
            }

            return current;
        }

        private static Stream CreateSingleDecompressionStream(
            Stream compressedStream,
            CompressionKind compression,
            bool wire,
            DecompressionBudget budget)
        {
            // Only the wire stream is owned by the caller (leaveOpen); every decoder owns the decoder below it.
            switch (compression)
            {
                case CompressionKind.Gzip:
                    // Member-aware gzip decoder (RFC 1952 section 2.2): checks every member's CRC32 + ISIZE itself,
                    // because Unity's Mono GZipStream can report a truncated member as a clean end of stream.
                    return new InflateLayerStream(compressedStream, leaveOpen: wire, gzip: true, budget, wire);
                case CompressionKind.Deflate:
                    // RFC 9110 section 8.4.1.2: zlib (RFC 1950) with its Adler-32 checked; raw DEFLATE as fallback.
                    return new InflateLayerStream(compressedStream, leaveOpen: wire, gzip: false, budget, wire);
                default:
                    throw new InvalidOperationException("Compression mode is not active.");
            }
        }

        private static UHttpException CreateDecompressionError(Exception ex)
        {
            return new UHttpException(
                new UHttpError(UHttpErrorType.Unknown, "Response decompression failed.", ex));
        }

        /// <summary>
        /// Builds the coding chain from every <c>Content-Encoding</c> field line in order: RFC 9110 section 5.3
        /// combines field lines of one name into a single comma-separated list, and section 8.4 lists the codings
        /// in the order they were applied (#782). Any coding this handler cannot decode passes the response through
        /// untouched; a chain longer than <see cref="MaxContentCodings"/> (<c>identity</c> not counted) resolves to
        /// <see cref="CompressionResolution.TooManyCodings"/> without growing past the cap (#783).
        /// </summary>
        private static CompressionResolution ResolveCompression(
            HttpHeaders headers,
            out CompressionKind[] compressionChain)
        {
            compressionChain = Array.Empty<CompressionKind>();
            if (headers == null)
                return CompressionResolution.PassThrough;

            var fieldLines = headers.GetValues("Content-Encoding");
            if (fieldLines.Count == 0)
                return CompressionResolution.PassThrough;

            CompressionKind[] resolved = null;
            var count = 0;
            for (var line = 0; line < fieldLines.Count; line++)
            {
                var contentEncoding = fieldLines[line];
                if (string.IsNullOrEmpty(contentEncoding))
                    continue;

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
                            // An unknown coding anywhere in the combined list: the body cannot be decoded here.
                            return CompressionResolution.PassThrough;
                        }

                        if (compression.HasValue)
                        {
                            // Keep scanning past the cap: an unknown coding later in the list still passes through.
                            if (count < MaxContentCodings)
                            {
                                resolved ??= new CompressionKind[MaxContentCodings];
                                resolved[count] = compression.Value;
                                count++;
                            }
                            else
                            {
                                count = MaxContentCodings + 1;
                            }
                        }
                    }

                    start = end + 1;
                }
            }

            if (count == 0)
                return CompressionResolution.PassThrough;

            if (count > MaxContentCodings)
                return CompressionResolution.TooManyCodings;

            if (count < resolved.Length)
                Array.Resize(ref resolved, count);

            compressionChain = resolved;
            return CompressionResolution.Decode;
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
    }
}
