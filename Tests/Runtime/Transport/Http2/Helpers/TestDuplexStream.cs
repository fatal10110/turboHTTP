using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Tests.Transport.Http2.Helpers
{
    /// <summary>
    /// A bidirectional in-memory stream pair for testing Http2Connection without real TCP.
    /// Writes on one side are readable on the other. Uses blocking collections for async handoff.
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
    /// Write enqueues bytes; Read dequeues them, blocking if empty.
    /// </summary>
    internal class BlockingStream
    {
        private readonly BlockingCollection<byte[]> _chunks = new BlockingCollection<byte[]>();
        private byte[] _currentChunk;
        private int _currentOffset;

        public void Write(byte[] buffer, int offset, int count)
        {
            if (count <= 0) return;
            var copy = new byte[count];
            Array.Copy(buffer, offset, copy, 0, count);
            _chunks.Add(copy);
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

            if (!_chunks.TryTake(out _currentChunk, Timeout.Infinite))
                return 0; // completed

            _currentOffset = 0;
            return ConsumeCurrentChunk(buffer, offset, count);
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
            // Use Task.Run to avoid blocking the caller
            _currentChunk = await Task.Run(() =>
            {
                try
                {
                    if (_chunks.TryTake(out var chunk, Timeout.Infinite, cancellationToken))
                        return chunk;
                    return null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }, cancellationToken);

            if (_currentChunk == null)
                return 0;

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
            _chunks.CompleteAdding();
        }
    }
}
