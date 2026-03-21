using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TurboHTTP.Core;

namespace TurboHTTP.Testing
{
    public sealed class MockResponseBodySource : IResponseBodySource
    {
        // Test-only single-reader source: sequential chunk cursors are intentionally not synchronized.
        private readonly ReadOnlyMemory<byte>[] _chunks;
        private readonly HttpHeaders _trailers;
        private readonly ReadOnlyMemory<byte> _bufferedData;
        private readonly bool _hasBufferedData;
        private readonly long? _length;

        private Exception _fault;
        private int _chunkIndex;
        private int _chunkOffset;
        private int _abortCount;
        private int _disposeAsyncCount;
        private int _disposed;
        private int _aborted;

        public MockResponseBodySource(
            ReadOnlyMemory<byte> bufferedData,
            long? length = null,
            HttpHeaders trailers = null)
            : this(
                new[] { bufferedData },
                length ?? bufferedData.Length,
                trailers,
                exposeBufferedData: true)
        {
        }

        public MockResponseBodySource(
            IEnumerable<ReadOnlyMemory<byte>> chunks,
            long? length = null,
            HttpHeaders trailers = null,
            bool exposeBufferedData = false)
        {
            if (chunks == null)
                throw new ArgumentNullException(nameof(chunks));

            var materializedChunks = MaterializeChunks(chunks, out var totalLength);
            _chunks = materializedChunks;
            _trailers = trailers?.Clone() ?? HttpHeaders.Empty;
            _length = length ?? totalLength;

            if (exposeBufferedData)
            {
                _bufferedData = CombineChunks(materializedChunks, totalLength);
                _hasBufferedData = true;
            }
        }

        public long? Length => _length;

        public int AbortCount => Volatile.Read(ref _abortCount);

        public int DisposeAsyncCount => Volatile.Read(ref _disposeAsyncCount);

        public void InjectFault(Exception error)
        {
            if (error == null)
                throw new ArgumentNullException(nameof(error));

            Interlocked.Exchange(ref _fault, error);
        }

        public bool TryGetBufferedData(out ReadOnlyMemory<byte> data)
        {
            ThrowIfUnavailable();

            if (_hasBufferedData)
            {
                data = _bufferedData;
                return true;
            }

            data = default;
            return false;
        }

        public bool TryDetachBufferedBody(out DetachedBufferedBody body)
        {
            ThrowIfUnavailable();
            ThrowIfFaulted();

            if (!_hasBufferedData || _chunkIndex != 0 || _chunkOffset != 0 || _trailers.Count != 0)
            {
                body = default;
                return false;
            }

            body = new DetachedBufferedBody(_bufferedData);
            Interlocked.Exchange(ref _disposed, 1);
            return true;
        }

        public ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            ThrowIfUnavailable();
            ct.ThrowIfCancellationRequested();
            ThrowIfFaulted();

            if (destination.IsEmpty)
                return new ValueTask<int>(0);

            while (_chunkIndex < _chunks.Length)
            {
                var remainingChunk = _chunks[_chunkIndex].Slice(_chunkOffset);
                if (remainingChunk.IsEmpty)
                {
                    _chunkIndex++;
                    _chunkOffset = 0;
                    continue;
                }

                var count = Math.Min(destination.Length, remainingChunk.Length);
                remainingChunk.Slice(0, count).CopyTo(destination);
                _chunkOffset += count;
                if (_chunkOffset >= _chunks[_chunkIndex].Length)
                {
                    _chunkIndex++;
                    _chunkOffset = 0;
                }

                return new ValueTask<int>(count);
            }

            return new ValueTask<int>(0);
        }

        public ValueTask DrainAsync(CancellationToken ct)
        {
            ThrowIfUnavailable();
            ct.ThrowIfCancellationRequested();
            ThrowIfFaulted();

            _chunkIndex = _chunks.Length;
            _chunkOffset = 0;
            return default;
        }

        public void Abort()
        {
            Interlocked.Increment(ref _abortCount);
            Interlocked.Exchange(ref _aborted, 1);
        }

        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct)
        {
            ThrowIfUnavailable();
            ct.ThrowIfCancellationRequested();
            ThrowIfFaulted();
            return new ValueTask<HttpHeaders>(_trailers);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return default;

            Interlocked.Increment(ref _disposeAsyncCount);
            return default;
        }

        private static ReadOnlyMemory<byte>[] MaterializeChunks(
            IEnumerable<ReadOnlyMemory<byte>> chunks,
            out int totalLength)
        {
            var list = new List<ReadOnlyMemory<byte>>();
            totalLength = 0;

            foreach (var chunk in chunks)
            {
                list.Add(chunk);
                checked
                {
                    totalLength += chunk.Length;
                }
            }

            return list.ToArray();
        }

        private static ReadOnlyMemory<byte> CombineChunks(ReadOnlyMemory<byte>[] chunks, int totalLength)
        {
            if (chunks.Length == 0)
                return ReadOnlyMemory<byte>.Empty;

            if (chunks.Length == 1)
                return chunks[0];

            var combined = new byte[totalLength];
            var offset = 0;
            for (var i = 0; i < chunks.Length; i++)
            {
                var chunk = chunks[i];
                chunk.CopyTo(combined.AsMemory(offset, chunk.Length));
                offset += chunk.Length;
            }

            return combined;
        }

        private void ThrowIfFaulted()
        {
            var error = Volatile.Read(ref _fault);
            if (error != null)
                throw error;
        }

        private void ThrowIfUnavailable()
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _aborted) != 0)
                throw new ObjectDisposedException(nameof(MockResponseBodySource));
        }
    }
}
