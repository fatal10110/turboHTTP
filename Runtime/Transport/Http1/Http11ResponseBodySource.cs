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
        private const int DefaultDrainBufferBytes = 8192;
        private const int BufferedDrainReuseThresholdBytes = 64 * 1024;
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

        private long _remainingContentLength;
        private long _chunkBytesRemaining;
        private long _decodedChunkBytes;
        private long _readToEndBytesRead;
        private bool _awaitingChunkTerminator;
        private bool _awaitingChunkTrailers;
        private int _terminalState; // 0 = active, 1 = completed, 2 = aborted/closed
        private CancellationTokenSource _cachedReadTokenSource;
        private CancellationToken _cachedCallerToken;
        private bool _hasCachedCallerToken;

        internal Http11ResponseBodySource(
            ParsedResponseHead head,
            ConnectionLease lease,
            CancellationToken transportReadToken,
            TimeSpan requestTimeout)
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

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfClosed();

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

            var buffer = ArrayPool<byte>.Shared.Rent(DefaultDrainBufferBytes);
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
            var read = await _reader.ReadAsync(destination.Slice(0, toRead), effectiveToken).ConfigureAwait(false);
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
                    var effectiveToken = GetEffectiveReadToken(ct);
                    var toRead = (int)Math.Min(destination.Length, _chunkBytesRemaining);
                    var read = await _reader.ReadAsync(destination.Slice(0, toRead), effectiveToken).ConfigureAwait(false);
                    if (read == 0)
                        throw new IOException("Unexpected end of stream");

                    _chunkBytesRemaining -= read;
                    if (_chunkBytesRemaining == 0)
                        _awaitingChunkTerminator = true;

                    return read;
                }

                if (_awaitingChunkTerminator)
                {
                    await _reader.ReadExpectedCrlfAsync(GetEffectiveReadToken(ct)).ConfigureAwait(false);
                    _awaitingChunkTerminator = false;
                    continue;
                }

                if (_awaitingChunkTrailers)
                {
                    await ReadAndDiscardChunkTrailersAsync(ct).ConfigureAwait(false);
                    CompleteBody();
                    return 0;
                }

                var chunkSize = await _reader.ReadChunkSizeAsync(
                        GetEffectiveReadToken(ct),
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
            var read = await _reader.ReadAsync(destination, effectiveToken).ConfigureAwait(false);
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
                var line = await _reader.ReadLineAsync(effectiveToken, 8192).ConfigureAwait(false);
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
                    return Interlocked.Read(ref _remainingContentLength) <= BufferedDrainReuseThresholdBytes;

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
                Interlocked.Read(ref _remainingContentLength) > BufferedDrainReuseThresholdBytes)
            {
                return false;
            }

            var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(DefaultDrainBufferBytes, BufferedDrainReuseThresholdBytes));
            long remainingBudget = BufferedDrainReuseThresholdBytes;
            try
            {
                while (Volatile.Read(ref _terminalState) == 0)
                {
                    if (remainingBudget <= 0)
                        return false;

                    var toRead = (int)Math.Min(buffer.Length, remainingBudget);
                    var read = await ReadAsync(buffer.AsMemory(0, toRead), ct).ConfigureAwait(false);
                    if (read == 0)
                        return true;

                    remainingBudget -= read;
                }

                return Volatile.Read(ref _terminalState) == 1;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
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
