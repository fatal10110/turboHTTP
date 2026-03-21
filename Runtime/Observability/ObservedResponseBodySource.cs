using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Observability
{
    internal readonly struct ObservedResponseBodyCompletion
    {
        internal ObservedResponseBodyCompletion(
            long totalBytesObserved,
            bool completedNaturally,
            bool aborted,
            Exception error)
        {
            TotalBytesObserved = totalBytesObserved;
            CompletedNaturally = completedNaturally;
            Aborted = aborted;
            Error = error;
        }

        internal long TotalBytesObserved { get; }
        internal bool CompletedNaturally { get; }
        internal bool Aborted { get; }
        internal Exception Error { get; }
    }

    internal sealed class ObservedResponseBodySource : IResponseBodySource
    {
        private readonly IResponseBodySource _inner;
        private readonly Action<ReadOnlyMemory<byte>> _onChunkRead;
        private readonly Action<ObservedResponseBodyCompletion> _onDispose;

        private long _totalBytesObserved;
        private bool _completedNaturally;
        private bool _aborted;
        private Exception _error;
        private int _disposed;

        internal ObservedResponseBodySource(
            IResponseBodySource inner,
            Action<ReadOnlyMemory<byte>> onChunkRead,
            Action<ObservedResponseBodyCompletion> onDispose)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _onChunkRead = onChunkRead;
            _onDispose = onDispose;
        }

        public long? Length => _inner.Length;

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            body = default;
            return false;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();

            try
            {
                var read = await _inner.ReadAsync(destination, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    _completedNaturally = true;
                    return 0;
                }

                _totalBytesObserved += read;
                _onChunkRead?.Invoke(destination.Slice(0, read));
                return read;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _error = _error ?? ex;
                Abort();
                throw;
            }
        }

        public async ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            byte[] rented = null;
            try
            {
                rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
                while (await ReadAsync(rented, ct).ConfigureAwait(false) != 0)
                {
                }
            }
            finally
            {
                if (rented != null)
                    ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public void Abort()
        {
            if (_aborted)
                return;

            _aborted = true;
            _inner.Abort();
        }

        public async ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();

            try
            {
                return await _inner.GetTrailersAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _error = _error ?? ex;
                Abort();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _error = _error ?? ex;
                throw;
            }
            finally
            {
                _onDispose?.Invoke(new ObservedResponseBodyCompletion(
                    _totalBytesObserved,
                    _completedNaturally,
                    _aborted,
                    _error));
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ObservedResponseBodySource));
        }
    }
}
