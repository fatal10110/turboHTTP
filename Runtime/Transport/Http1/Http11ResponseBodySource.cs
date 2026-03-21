using System;
using System.Buffers;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;
using TurboHTTP.Transport.Tcp;

namespace TurboHTTP.Transport.Http1
{
    // HTTP/1.1 body sources are single-consumer. Reads and disposal/abort are expected
    // to be serialized by the caller; cancellation/error transitions are terminal.
    internal sealed class Http11ResponseBodySource : IResponseBodySource
    {
        private const int MaxChunkLineLength = 256;
        private const int MaxResponseBodySize = 100 * 1024 * 1024;
        private const int DefaultDrainBufferBytes = 64 * 1024;
        private const int BufferedDrainReuseThresholdBytes = 64 * 1024;
        private const int ChunkedDrainWireBudgetMultiplier = 5;
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

        private readonly Http11ResponseParser.BufferedStreamReader _reader;
        private readonly ConnectionLease _lease;
        private readonly Http11ResponseBodyKind _bodyKind;
        private readonly bool _keepAlive;
        // Buffered collectors pass the request-timeout token here; streaming callers pass None
        // once headers are delivered so body reads are governed only by the consumer token.
        private readonly CancellationToken _transportReadToken;
        private readonly double _requestTimeoutSeconds;
        private readonly long? _length;
        private readonly object _tokenLock = new object();
        private readonly int _drainBufferBytes;
        private readonly int _bufferedDrainReuseThresholdBytes;

        private long _remainingContentLength;
        private long _chunkBytesRemaining;
        private long _decodedChunkBytes;
        private long _readToEndBytesRead;
        private bool _awaitingChunkTerminator;
        private bool _awaitingChunkTrailers;
        private int _hasReadData;
        private int _terminalState; // 0 = active, 1 = completed, 2 = aborted/closed
        private CancellationTokenSource _cachedReadTokenSource;
        private CancellationToken _cachedCallerToken;
        private bool _hasCachedCallerToken;

        internal Http11ResponseBodySource(
            ParsedResponseHead head,
            ConnectionLease lease,
            CancellationToken transportReadToken,
            TimeSpan requestTimeout,
            StreamingOptions streamingOptions = null)
        {
            if (head == null)
                throw new ArgumentNullException(nameof(head));
            if (lease == null)
                throw new ArgumentNullException(nameof(lease));

            _reader = head.TransferReaderOwnership();
            _lease = lease;
            _bodyKind = head.BodyKind;
            _keepAlive = head.KeepAlive;
            _transportReadToken = transportReadToken;
            _requestTimeoutSeconds = requestTimeout.TotalSeconds;
            _drainBufferBytes = streamingOptions?.DefaultStreamingReceiveBufferBytes ?? DefaultDrainBufferBytes;
            _bufferedDrainReuseThresholdBytes = streamingOptions?.BufferedDrainReuseThresholdBytes ?? BufferedDrainReuseThresholdBytes;

            if (head.BodyKind == Http11ResponseBodyKind.ContentLength)
            {
                _remainingContentLength = head.ContentLength.GetValueOrDefault();
                _length = head.ContentLength.GetValueOrDefault();
            }
            else if (head.BodyKind == Http11ResponseBodyKind.Empty)
            {
                _length = 0;
            }

            if (_bodyKind == Http11ResponseBodyKind.Empty ||
                (_bodyKind == Http11ResponseBodyKind.ContentLength && _remainingContentLength == 0))
            {
                CompleteBody();
            }
        }

        public long? Length => _length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            ThrowIfClosed();

            if (Volatile.Read(ref _hasReadData) != 0)
            {
                body = default;
                return false;
            }

            if (Volatile.Read(ref _terminalState) == 1 &&
                (_bodyKind == Http11ResponseBodyKind.Empty ||
                 (_bodyKind == Http11ResponseBodyKind.ContentLength &&
                  _length.GetValueOrDefault() == 0)))
            {
                // Empty-body cases reach terminal completion through CompleteBody(), which already
                // returned the connection lease to the pool (or disposed it for non-keepalive).
                body = default;
                return true;
            }

            body = default;
            return false;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfClosed();
            Interlocked.Exchange(ref _hasReadData, 1);

            if (destination.IsEmpty)
                return 0;

            if (Volatile.Read(ref _terminalState) == 1)
                return 0;

            try
            {
                switch (_bodyKind)
                {
                    case Http11ResponseBodyKind.Empty:
                        return 0;

                    case Http11ResponseBodyKind.ContentLength:
                        return await ReadContentLengthAsync(destination, ct).ConfigureAwait(false);

                    case Http11ResponseBodyKind.Chunked:
                        return await ReadChunkedAsync(destination, ct).ConfigureAwait(false);

                    case Http11ResponseBodyKind.ReadToEnd:
                        return await ReadToEndAsync(destination, ct).ConfigureAwait(false);

                    default:
                        throw new InvalidOperationException($"Unsupported HTTP/1.1 body kind: {_bodyKind}");
                }
            }
            catch (OperationCanceledException ex)
            {
                CloseBody();
                if (_transportReadToken.CanBeCanceled &&
                    _transportReadToken.IsCancellationRequested &&
                    !ct.IsCancellationRequested)
                {
                    throw CreateTimeoutException(ex);
                }

                throw;
            }
            catch (FormatException ex)
            {
                CloseBody();
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"Malformed HTTP response: {ex.Message}",
                    ex));
            }
            catch (NotSupportedException ex)
            {
                CloseBody();
                throw new UHttpException(new UHttpError(
                    UHttpErrorType.NetworkError,
                    $"Unsupported HTTP response: {ex.Message}",
                    ex));
            }
            catch (IOException ex)
            {
                CloseBody();
                if (_transportReadToken.CanBeCanceled &&
                    _transportReadToken.IsCancellationRequested &&
                    !ct.IsCancellationRequested)
                {
                    throw CreateTimeoutException(ex);
                }

                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));
            }
            catch (SocketException ex)
            {
                CloseBody();
                if (_transportReadToken.CanBeCanceled &&
                    _transportReadToken.IsCancellationRequested &&
                    !ct.IsCancellationRequested)
                {
                    throw CreateTimeoutException(ex);
                }

                throw new UHttpException(new UHttpError(UHttpErrorType.NetworkError, ex.Message, ex));
            }
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfClosed();

            if (Volatile.Read(ref _terminalState) == 1)
                return;

            Interlocked.Exchange(ref _hasReadData, 1);

            var buffer = ArrayPool<byte>.Shared.Rent(_drainBufferBytes);
            try
            {
                while (Volatile.Read(ref _terminalState) == 0)
                {
                    var read = await ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0)
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public void Abort()
        {
            CloseBody();
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfClosed();

            if (Volatile.Read(ref _terminalState) == 0)
                await DrainAsync(ct).ConfigureAwait(false);

            return HttpHeaders.Empty;
        }

        public async ValueTask DisposeAsync()
        {
            var state = Volatile.Read(ref _terminalState);
            if (state != 0)
                return;

            if (!ShouldAttemptDisposeDrain())
            {
                CloseBody();
                return;
            }

            CancellationTokenSource timeoutCts = null;
            try
            {
                timeoutCts = _transportReadToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_transportReadToken)
                    : new CancellationTokenSource();
                timeoutCts.CancelAfter(DrainTimeout);

                var drained = await DrainWithinBudgetAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!drained)
                    CloseBody();
            }
            catch
            {
                CloseBody();
            }
            finally
            {
                timeoutCts?.Dispose();
            }
        }

        private async ValueTask<int> ReadContentLengthAsync(Memory<byte> destination, CancellationToken ct)
        {
            var remainingContentLength = Interlocked.Read(ref _remainingContentLength);
            if (remainingContentLength == 0)
                return 0;

            var effectiveToken = GetEffectiveReadToken(ct);
            var toRead = (int)Math.Min(destination.Length, remainingContentLength);
            var read = await ReadFromReaderAsync(destination.Slice(0, toRead), effectiveToken, ct).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("Unexpected end of stream");

            if (Interlocked.Add(ref _remainingContentLength, -read) == 0)
                CompleteBody();

            return read;
        }

        private async ValueTask<int> ReadChunkedAsync(Memory<byte> destination, CancellationToken ct)
        {
            while (true)
            {
                if (_chunkBytesRemaining > 0)
                {
                    var readToken = GetEffectiveReadToken(ct);
                    var toRead = (int)Math.Min(destination.Length, _chunkBytesRemaining);
                    var read = await ReadFromReaderAsync(destination.Slice(0, toRead), readToken, ct).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Unexpected end of stream");

                    _chunkBytesRemaining -= read;
                    if (_chunkBytesRemaining == 0)
                        _awaitingChunkTerminator = true;

                    return read;
                }

                if (_awaitingChunkTerminator)
                {
                    var terminatorToken = GetEffectiveReadToken(ct);
                    await ReadExpectedCrlfAsync(terminatorToken, ct).ConfigureAwait(false);
                    _awaitingChunkTerminator = false;
                    continue;
                }

                if (_awaitingChunkTrailers)
                {
                    await ReadAndDiscardChunkTrailersAsync(ct).ConfigureAwait(false);
                    CompleteBody();
                    return 0;
                }

                var effectiveToken = GetEffectiveReadToken(ct);
                var chunkSize = await ReadChunkSizeAsync(
                        effectiveToken,
                        ct,
                        MaxChunkLineLength)
                    .ConfigureAwait(false);
                if (chunkSize == 0)
                {
                    _awaitingChunkTrailers = true;
                    continue;
                }

                if (chunkSize > MaxResponseBodySize)
                    throw new IOException("Response body exceeds maximum size");

                _decodedChunkBytes += chunkSize;
                if (_decodedChunkBytes > MaxResponseBodySize)
                    throw new IOException("Response body exceeds maximum size");

                _chunkBytesRemaining = chunkSize;
            }
        }

        private async ValueTask<int> ReadToEndAsync(Memory<byte> destination, CancellationToken ct)
        {
            var effectiveToken = GetEffectiveReadToken(ct);
            var read = await ReadFromReaderAsync(destination, effectiveToken, ct).ConfigureAwait(false);
            if (read == 0)
            {
                CompleteBody();
                return 0;
            }

            _readToEndBytesRead += read;
            if (_readToEndBytesRead > MaxResponseBodySize)
                throw new IOException("Response body exceeds maximum size");

            return read;
        }

        private async ValueTask ReadAndDiscardChunkTrailersAsync(CancellationToken ct)
        {
            var effectiveToken = GetEffectiveReadToken(ct);
            while (true)
            {
                var line = await ReadLineAsync(effectiveToken, ct, 8192).ConfigureAwait(false);
                if (string.IsNullOrEmpty(line))
                    return;
            }
        }

        private bool ShouldAttemptDisposeDrain()
        {
            if (!_keepAlive)
                return false;

            switch (_bodyKind)
            {
                case Http11ResponseBodyKind.ContentLength:
                    return Interlocked.Read(ref _remainingContentLength) <= _bufferedDrainReuseThresholdBytes;

                case Http11ResponseBodyKind.Chunked:
                    // Unread decoded length is not known up front for chunked bodies. Opt into
                    // the drain attempt here and let DrainWithinBudgetAsync enforce the 64 KB cap.
                    return true;

                default:
                    return false;
            }
        }

        private async ValueTask<bool> DrainWithinBudgetAsync(CancellationToken ct)
        {
            if (_bodyKind == Http11ResponseBodyKind.ContentLength &&
                Interlocked.Read(ref _remainingContentLength) > _bufferedDrainReuseThresholdBytes)
            {
                return false;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(_drainBufferBytes, _bufferedDrainReuseThresholdBytes));
            long remainingBudget = _bufferedDrainReuseThresholdBytes;
            try
            {
                while (Volatile.Read(ref _terminalState) == 0)
                {
                    if (remainingBudget <= 0)
                        return false;

                    var maxDecodedRead = GetMaxDrainReadBytes(remainingBudget);
                    if (maxDecodedRead <= 0)
                        return false;

                    var toRead = (int)Math.Min(buffer.Length, maxDecodedRead);
                    var read = await ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read == 0)
                        return true;

                    remainingBudget -= GetDrainBudgetCharge(read);
                }

                return Volatile.Read(ref _terminalState) == 1;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private ValueTask<int> ReadFromReaderAsync(
            Memory<byte> destination,
            CancellationToken effectiveToken,
            CancellationToken callerToken)
        {
            return AwaitReaderOperationAsync(
                _reader.ReadAsync(destination, effectiveToken),
                effectiveToken,
                callerToken);
        }

        private ValueTask<long> ReadChunkSizeAsync(
            CancellationToken effectiveToken,
            CancellationToken callerToken,
            int maxLength)
        {
            return AwaitReaderOperationAsync(
                _reader.ReadChunkSizeAsync(effectiveToken, maxLength),
                effectiveToken,
                callerToken);
        }

        private ValueTask<string> ReadLineAsync(
            CancellationToken effectiveToken,
            CancellationToken callerToken,
            int maxLength)
        {
            return AwaitReaderOperationAsync(
                new ValueTask<string>(_reader.ReadLineAsync(effectiveToken, maxLength)),
                effectiveToken,
                callerToken);
        }

        private ValueTask ReadExpectedCrlfAsync(
            CancellationToken effectiveToken,
            CancellationToken callerToken)
        {
            return AwaitReaderOperationAsync(
                _reader.ReadExpectedCrlfAsync(effectiveToken),
                effectiveToken,
                callerToken);
        }

        private async ValueTask<T> AwaitReaderOperationAsync<T>(
            ValueTask<T> pending,
            CancellationToken effectiveToken,
            CancellationToken callerToken)
        {
            if (!effectiveToken.CanBeCanceled || pending.IsCompleted)
                return await pending.ConfigureAwait(false);

            using var registration = effectiveToken.Register(
                static state => ((Http11ResponseBodySource)state).CloseBody(),
                this);

            try
            {
                return await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
            catch (ObjectDisposedException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
            catch (IOException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
        }

        private async ValueTask AwaitReaderOperationAsync(
            ValueTask pending,
            CancellationToken effectiveToken,
            CancellationToken callerToken)
        {
            if (!effectiveToken.CanBeCanceled || pending.IsCompleted)
            {
                await pending.ConfigureAwait(false);
                return;
            }

            using var registration = effectiveToken.Register(
                static state => ((Http11ResponseBodySource)state).CloseBody(),
                this);

            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
            catch (ObjectDisposedException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
            catch (IOException) when (effectiveToken.IsCancellationRequested)
            {
                throw CreateCanceledReadException(effectiveToken, callerToken);
            }
        }

        private static OperationCanceledException CreateCanceledReadException(
            CancellationToken effectiveToken,
            CancellationToken callerToken)
        {
            if (callerToken.CanBeCanceled && callerToken.IsCancellationRequested)
                return new OperationCanceledException(callerToken);

            return new OperationCanceledException(effectiveToken);
        }

        private long GetDrainBudgetCharge(int decodedBytesRead)
        {
            if (decodedBytesRead <= 0)
                return 0;

            if (_bodyKind != Http11ResponseBodyKind.Chunked)
                return decodedBytesRead;

            // Chunked drains need to budget for on-wire framing, not just decoded payload bytes.
            // A 1-byte chunk consumes 5 bytes on the wire ("1\\r\\n" + data + "\\r\\n"), so use
            // that worst-case multiplier to keep the keep-alive reuse probe conservative.
            return (long)decodedBytesRead * ChunkedDrainWireBudgetMultiplier;
        }

        private long GetMaxDrainReadBytes(long remainingBudget)
        {
            if (_bodyKind != Http11ResponseBodyKind.Chunked)
                return remainingBudget;

            if (remainingBudget < ChunkedDrainWireBudgetMultiplier)
                return 0;

            return remainingBudget / ChunkedDrainWireBudgetMultiplier;
        }

        private CancellationToken GetEffectiveReadToken(CancellationToken ct)
        {
            if (!_transportReadToken.CanBeCanceled || !ct.CanBeCanceled || ct == _transportReadToken)
                return _transportReadToken.CanBeCanceled ? _transportReadToken : ct;

            lock (_tokenLock)
            {
                if (_cachedReadTokenSource != null &&
                    _hasCachedCallerToken &&
                    _cachedCallerToken == ct)
                {
                    return _cachedReadTokenSource.Token;
                }

                _cachedReadTokenSource?.Dispose();
                _cachedReadTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct, _transportReadToken);
                _cachedCallerToken = ct;
                _hasCachedCallerToken = true;
                return _cachedReadTokenSource.Token;
            }
        }

        private void CompleteBody()
        {
            if (Interlocked.CompareExchange(ref _terminalState, 1, 0) != 0)
                return;

            DisposeCachedReadTokenSource();
            _reader.Dispose();
            if (_keepAlive)
            {
                _lease.ReturnToPool();
                return;
            }

            _lease.Dispose();
        }

        private void CloseBody()
        {
            if (Interlocked.CompareExchange(ref _terminalState, 2, 0) != 0)
                return;

            DisposeCachedReadTokenSource();
            _reader.Dispose();
            _lease.Dispose();
        }

        private void ThrowIfClosed()
        {
            if (Volatile.Read(ref _terminalState) == 2)
                throw new ObjectDisposedException(nameof(Http11ResponseBodySource));
        }

        private void DisposeCachedReadTokenSource()
        {
            CancellationTokenSource toDispose;
            lock (_tokenLock)
            {
                toDispose = _cachedReadTokenSource;
                _cachedReadTokenSource = null;
                _cachedCallerToken = default;
                _hasCachedCallerToken = false;
            }

            toDispose?.Dispose();
        }

        private UHttpException CreateTimeoutException(Exception inner)
        {
            return new UHttpException(new UHttpError(
                UHttpErrorType.Timeout,
                $"Request timed out after {_requestTimeoutSeconds}s",
                inner));
        }
    }
}
