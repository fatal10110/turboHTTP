using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace TurboHTTP.Core
{
    public sealed class UHttpStreamingResponse : IAsyncDisposable, IDisposable
    {
        private readonly IResponseBodySource _bodySource;
        private readonly ResponseBodyStream _body;
        private readonly object _disposeCallbackGate = new object();
        private Action _onDispose;
        private int _disposed;

        public UHttpStreamingResponse(
            HttpStatusCode statusCode,
            HttpHeaders headers,
            IResponseBodySource bodySource)
        {
            StatusCode = statusCode;
            Headers = headers ?? new HttpHeaders();
            _bodySource = bodySource ?? throw new ArgumentNullException(nameof(bodySource));
            _body = new ResponseBodyStream(this);
        }

        public HttpStatusCode StatusCode { get; }

        public HttpHeaders Headers { get; }

        public ResponseBodyStream Body => _body;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal long? BodyLength => _bodySource.Length;

        internal IResponseBodySource BodySourceForTesting => _bodySource;

        internal ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken ct)
        {
            return _bodySource.ReadAsync(destination, ct);
        }

        /// <summary>
        /// Returns HTTP trailers after the response body has reached completion.
        /// Calling this before EOF may wait for the remaining body to be consumed or for transport
        /// cleanup to complete; on HTTP/1.1 responses without trailer support this returns
        /// <see cref="HttpHeaders.Empty"/>.
        /// </summary>
        public ValueTask<HttpHeaders> GetTrailersAsync(CancellationToken ct = default)
        {
            ThrowIfDisposed();
            return _bodySource.GetTrailersAsync(ct);
        }

        ~UHttpStreamingResponse()
        {
#if DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            if (Volatile.Read(ref _disposed) == 0)
            {
                Debug.WriteLine(
                    "[TurboHTTP] UHttpStreamingResponse was not disposed. Response bodies must be disposed to release transport resources.");
            }
#endif
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            GC.SuppressFinalize(this);

            try
            {
                DisposeBodySynchronously();
            }
            finally
            {
                InvokeDisposeCallbacks();
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return default;

            GC.SuppressFinalize(this);
            return DisposeAsyncCore();
        }

        internal void AttachRequestRelease(Action releaseAction)
        {
            if (releaseAction == null)
                return;

            var releaseImmediately = false;
            lock (_disposeCallbackGate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    releaseImmediately = true;
                }
                else
                {
                    _onDispose = (Action)Delegate.Combine(_onDispose, releaseAction);
                }
            }

            if (releaseImmediately)
                releaseAction();
        }

        internal void AbortBody()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _bodySource.Abort();
        }

        private void DisposeBodySynchronously()
        {
            if (!_body.HasReachedEndOfStream)
            {
                _bodySource.Abort();
                return;
            }

            try
            {
                var disposeTask = _bodySource.DisposeAsync();
                if (disposeTask.IsCompletedSuccessfully)
                {
                    disposeTask.GetAwaiter().GetResult();
                }
                else
                {
                    disposeTask.AsTask().GetAwaiter().GetResult();
                }
            }
            catch
            {
                _bodySource.Abort();
            }
        }

        private async ValueTask DisposeAsyncCore()
        {
            try
            {
                await _bodySource.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                InvokeDisposeCallbacks();
            }
        }

        private void InvokeDisposeCallbacks()
        {
            Action callbacks;
            lock (_disposeCallbackGate)
            {
                callbacks = _onDispose;
                _onDispose = null;
            }

            callbacks?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpStreamingResponse));
        }
    }
}
