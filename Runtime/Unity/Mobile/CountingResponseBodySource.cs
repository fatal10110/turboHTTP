using System;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Unity.Mobile
{
    /// <summary>
    /// Forwards a response body and counts the bytes the consumer takes from it: bytes returned by
    /// <see cref="ReadAsync"/>, the length of a detached buffered sequence (no copy), or the length
    /// of a buffered view the consumer read without reading the stream. <see cref="DrainAsync"/> is
    /// forwarded to the inner source, which may discard without reading (an HTTP/2 stream is reset),
    /// so drained bytes are not counted. <c>onComplete</c> runs once, with the count, at the first of
    /// end of stream, detach, drain, read failure or dispose (#261).
    /// </summary>
    /// <remarks>
    /// The source keeps the single-consumer contract of <see cref="IResponseBodySource"/>: it owns
    /// nothing beyond the inner source, which it disposes exactly once when it is disposed.
    /// </remarks>
    internal sealed class CountingResponseBodySource : IResponseBodySource
    {
        private readonly IResponseBodySource _inner;
        private readonly Action<long> _onComplete;

        private long _bytesRead;
        private long _bufferedViewLength;
        private int _completed;
        private int _disposed;

        internal CountingResponseBodySource(IResponseBodySource inner, Action<long> onComplete)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onComplete = onComplete;
        }

        public long? Length => _inner.Length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            if (!_inner.TryGetBufferedData(out data))
                return false;

            // A view can be taken more than once; it counts its length, not once per call.
            Interlocked.Exchange(ref _bufferedViewLength, data.Length);
            return true;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            if (!_inner.TryDetachBufferedBody(out body))
                return false;

            Interlocked.Add(ref _bytesRead, body.Sequence.Length);
            Complete();
            return true;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            int read;
            try
            {
                read = await _inner.ReadAsync(destination, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                Complete();
                throw;
            }

            if (read > 0)
                Interlocked.Add(ref _bytesRead, read);
            else if (!destination.IsEmpty)
                Complete();

            return read;
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            // Forwarded, never read here: reading would turn a discard (HTTP/2 resets the stream)
            // into a full download.
            try
            {
                await _inner.DrainAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Complete();
            }
        }

        public void Abort()
        {
            _inner.Abort();
        }

        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            return _inner.GetTrailersAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Complete();
            }
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
                return;

            var counted = Math.Max(Interlocked.Read(ref _bytesRead), Interlocked.Read(ref _bufferedViewLength));
            _onComplete?.Invoke(counted);
        }
    }
}
