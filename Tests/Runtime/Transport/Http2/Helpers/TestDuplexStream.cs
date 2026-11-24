using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport.Http2.Helpers
{
    /// <summary>
    /// A bidirectional in-memory stream pair for testing Http2Connection without real TCP.
    /// Writes on one side are readable on the other; a pending async read holds no thread.
    /// </summary>
    internal class TestDuplexStream
    {
        public Stream ClientStream { get; }
        public Stream ServerStream { get; }

        public TestDuplexStream()
        {
            var clientToServer = new BlockingStream();
            var serverToClient = new BlockingStream();

            ClientStream = new DuplexStreamEndpoint(serverToClient, clientToServer);
            ServerStream = new DuplexStreamEndpoint(clientToServer, serverToClient);
        }

        /// <summary>
        /// One endpoint: reads from one BlockingStream, writes to another.
        /// </summary>
        private class DuplexStreamEndpoint : Stream
        {
            private readonly BlockingStream _readSource;
            private readonly BlockingStream _writeTarget;
            private volatile bool _disposed;

            public DuplexStreamEndpoint(BlockingStream readSource, BlockingStream writeTarget)
            {
                _readSource = readSource;
                _writeTarget = writeTarget;
            }

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return _readSource.Read(buffer, offset, count);
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                return _readSource.ReadAsync(buffer, offset, count, cancellationToken);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _writeTarget.Write(buffer, offset, count);
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count,
                CancellationToken cancellationToken)
            {
                return _writeTarget.WriteAsync(buffer, offset, count, cancellationToken);
            }

            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    _writeTarget.CompleteWriting();
                }
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>
    /// A thread-safe byte queue that supports async Read/Write.
    /// Write enqueues bytes; Read dequeues them, waiting while empty. A pending <see cref="ReadAsync"/>
    /// awaits a semaphore and holds no thread: the earlier Task.Run around BlockingCollection.TryTake parked
    /// a thread-pool thread per pending read, so every HTTP/2 connection the tests left reading held one,
    /// and on the IL2CPP player (4 minimum workers, slow injection) they starved the pool for minutes (#1297).
    /// </summary>
    internal class BlockingStream
    {
        // The written chunks, then a null end marker after CompleteWriting; _available counts the queued entries.
        private readonly ConcurrentQueue<byte[]> _chunks = new ConcurrentQueue<byte[]>();
        private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
        private readonly object _gate = new object();
        private bool _completed;
        private byte[] _currentChunk;
        private int _currentOffset;

        public void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            var copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);
            lock (_gate)
            {
                // As BlockingCollection.Add after CompleteAdding.
                if (_completed)
                    throw new InvalidOperationException("The stream was completed for writing.");
                _chunks.Enqueue(copy);
            }
            _available.Release();
        }

        public Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (_currentChunk != null && _currentOffset < _currentChunk.Length)
                return ConsumeCurrentChunk(buffer, offset, count);

            _available.Wait();
            return TakeChunk(buffer, offset, count);
        }

        public Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            if (_currentChunk != null && _currentOffset < _currentChunk.Length)
                return Task.FromResult(ConsumeCurrentChunk(buffer, offset, count));

            return ReadAsyncCore(buffer, offset, count, cancellationToken);
        }

        private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
            return TakeChunk(buffer, offset, count);
        }

        // Called after the semaphore granted one queued entry, so the dequeue always finds it.
        private int TakeChunk(byte[] buffer, int offset, int count)
        {
            _chunks.TryDequeue(out var chunk);
            if (chunk == null)
            {
                // The end marker: put it back so every later read ends too.
                _chunks.Enqueue(null);
                _available.Release();
                _currentChunk = null;
                return 0;
            }

            _currentChunk = chunk;
            _currentOffset = 0;
            return ConsumeCurrentChunk(buffer, offset, count);
        }

        private int ConsumeCurrentChunk(byte[] buffer, int offset, int count)
        {
            int available = _currentChunk.Length - _currentOffset;
            int toCopy = Math.Min(available, count);
            Array.Copy(_currentChunk, _currentOffset, buffer, offset, toCopy);
            _currentOffset += toCopy;
            return toCopy;
        }

        public void CompleteWriting()
        {
            lock (_gate)
            {
                if (_completed) return;
                _completed = true;
                _chunks.Enqueue(null);
            }
            _available.Release();
        }
    }
}
