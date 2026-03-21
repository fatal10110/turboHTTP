using System;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core.Internal
{
    internal sealed class RequestBodyReadSession : IDisposable
    {
        private readonly bool _disposeStream;
        private readonly Action _onDispose;
        private readonly Action _onDisposeFailure;
        private int _disposed;

        internal RequestBodyReadSession(
            Stream stream,
            long? contentLength,
            bool disposeStream = true,
            Action onDispose = null,
            Action onDisposeFailure = null)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (contentLength < 0)
                throw new ArgumentOutOfRangeException(nameof(contentLength));

            Stream = stream;
            ContentLength = contentLength;
            _disposeStream = disposeStream;
            _onDispose = onDispose;
            _onDisposeFailure = onDisposeFailure;
        }

        internal Stream Stream { get; }

        internal long? ContentLength { get; }

        internal ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(RequestBodyReadSession));
            if (destination.IsEmpty)
                return new ValueTask<int>(0);

            return Stream.ReadAsync(destination, ct);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            Exception disposeException = null;
            if (_disposeStream)
            {
                try
                {
                    Stream.Dispose();
                }
                catch (Exception ex)
                {
                    _onDisposeFailure?.Invoke();
                    disposeException = ex;
                }
            }

            try
            {
                _onDispose?.Invoke();
            }
            finally
            {
                if (disposeException != null)
                    ExceptionDispatchInfo.Capture(disposeException).Throw();
            }
        }
    }
}
