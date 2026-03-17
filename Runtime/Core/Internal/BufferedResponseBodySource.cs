using System;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    internal sealed class BufferedResponseBodySource : IResponseBodySource
    {
        private ReadOnlyMemory<byte> _data;
        private HttpHeaders _trailers;
        private Action _onDispose;
        private int _offset;
        private int _disposed;

        internal BufferedResponseBodySource(
            ReadOnlyMemory<byte> data,
            HttpHeaders trailers = null,
            Action onDispose = null)
        {
            _data = data;
            _trailers = trailers?.Clone() ?? HttpHeaders.Empty;
            _onDispose = onDispose;
        }

        public long? Length => ThrowIfDisposedAndGetLength();

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            ThrowIfDisposed();
            data = _data;
            return true;
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();

            if (destination.IsEmpty)
                return new ValueTask<int>(0);

            if (_offset >= _data.Length)
                return new ValueTask<int>(0);

            var count = Math.Min(destination.Length, _data.Length - _offset);
            _data.Slice(_offset, count).CopyTo(destination);
            _offset += count;
            return new ValueTask<int>(count);
        }

        public ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            _offset = _data.Length;
            return default;
        }

        public void Abort()
        {
            DisposeCore();
        }

        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfDisposed();
            ct.ThrowIfCancellationRequested();
            return new ValueTask<HttpHeaders>(_trailers);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCore();
            return default;
        }

        private long ThrowIfDisposedAndGetLength()
        {
            ThrowIfDisposed();
            return _data.Length;
        }

        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _offset = _data.Length;
            _data = default;
            _trailers = HttpHeaders.Empty;
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(BufferedResponseBodySource));
        }
    }
}
