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

        internal ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken ct)
        {
            return _bodySource.ReadAsync(destination, ct);
        }

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
                _bodySource.Abort();
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

            Action prior;
            Action combined;
            do
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    releaseAction();
                    return;
                }

                prior = _onDispose;
                combined = (Action)Delegate.Combine(prior, releaseAction);
            }
            while (Interlocked.CompareExchange(ref _onDispose, combined, prior) != prior);
        }

        internal void AbortBody()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _bodySource.Abort();
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
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(UHttpStreamingResponse));
        }
    }
}
