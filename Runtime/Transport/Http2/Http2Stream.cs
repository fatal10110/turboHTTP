using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using TurboHTTP.Core;

namespace TurboHTTP.Transport.Http2
{
    internal enum Http2StreamState
    {
        Idle,
        Open,
        HalfClosedLocal,
        HalfClosedRemote,
        Closed
    }

    /// <summary>
    /// Represents a single HTTP/2 stream — one request/response pair multiplexed
    /// on a shared TCP connection. RFC 7540 Section 5.1.
    /// </summary>
    /// <remarks>
    /// Instances are obtained from <see cref="Http2StreamPool"/> rather than allocated
    /// directly. The pool reuses the internal <see cref="HeaderBlockBuffer"/> (a
    /// <see cref="MemoryStream"/> allocated once per instance) across streams by
    /// calling <see cref="Initialize"/> for each new request. This avoids the
    /// MemoryStream allocation on the per-request hot path.
    /// </remarks>
    internal sealed class Http2Stream : IDisposable, IValueTaskSource
    {
        private struct VoidResult
        {
        }

        private int _streamId;
        private UHttpRequest _request;
        private RequestContext _context;
        private IHttpHandler _handler;
        private HttpHeaders _trailers;
        private int _responseBodyLength;
        private int _disposed;
        private int _handlerFaulted;
        private int _responseCompleted;
        private int _sendWindowSize;
        private ManualResetValueTaskSourceCore<VoidResult> _completionSource;

        public int StreamId => _streamId;
        public UHttpRequest Request => _request;
        public RequestContext Context => _context;
        public Http2StreamState State { get; set; }
        public int ResponseBodyLength => _responseBodyLength;
        public int ResponseBodyCapacity => 0;

        /// <summary>
        /// Buffer for accumulating HEADERS + CONTINUATION header blocks.
        /// Allocated once in the constructor; reset via <see cref="MemoryStream.SetLength"/>
        /// between pool uses rather than disposed and reallocated.
        /// </summary>
        public MemoryStream HeaderBlockBuffer { get; }

        public bool HeadersReceived { get; set; }
        public bool PendingEndStream { get; set; }
        public bool HasHandlerFault => Volatile.Read(ref _handlerFaulted) != 0;

        public int SendWindowSize
        {
            get => Interlocked.CompareExchange(ref _sendWindowSize, 0, 0);
            set => Interlocked.Exchange(ref _sendWindowSize, value);
        }

        public int AdjustSendWindowSize(int delta) => Interlocked.Add(ref _sendWindowSize, delta);

        /// <summary>
        /// Stream-level receive window. Tracks how many bytes the peer is allowed to send
        /// on this stream. Decremented when DATA arrives, replenished via WINDOW_UPDATE.
        /// Only accessed from the single read loop thread.
        /// </summary>
        public int RecvWindowSize { get; set; }

        public ValueTask CompletionTask => new ValueTask(this, _completionSource.Version);
        public bool IsResponseCompleted => Volatile.Read(ref _responseCompleted) != 0;
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        internal Http2Stream()
        {
            HeaderBlockBuffer = new MemoryStream();
            _handler = TurboHTTP.Transport.NullHandler.Instance;
            _completionSource = new ManualResetValueTaskSourceCore<VoidResult>
            {
                RunContinuationsAsynchronously = true
            };
            _completionSource.Reset();
            _disposed = 1;
        }

        public Http2Stream(
            int streamId,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize)
            : this()
        {
            Initialize(streamId, request, handler, context, initialSendWindowSize, initialRecvWindowSize);
        }

        public void Initialize(
            int streamId,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize)
        {
            _streamId = streamId;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _trailers = null;
            _responseBodyLength = 0;
            HeadersReceived = false;
            PendingEndStream = false;
            State = Http2StreamState.Idle;
            HeaderBlockBuffer.SetLength(0);
            _sendWindowSize = initialSendWindowSize;
            RecvWindowSize = initialRecvWindowSize;
            CancellationRegistration = default;
            Interlocked.Exchange(ref _handlerFaulted, 0);
            Interlocked.Exchange(ref _responseCompleted, 0);
            Volatile.Write(ref _disposed, 0);
        }

        public void PrepareForPool()
        {
            Volatile.Write(ref _disposed, 1);
            CancellationRegistration.Dispose();
            CancellationRegistration = default;
            HeaderBlockBuffer.SetLength(0);
            _streamId = 0;
            _request = null;
            _context = null;
            _handler = TurboHTTP.Transport.NullHandler.Instance;
            _trailers = null;
            _responseBodyLength = 0;
            HeadersReceived = false;
            PendingEndStream = false;
            State = Http2StreamState.Idle;
            _sendWindowSize = 0;
            RecvWindowSize = 0;
            Interlocked.Exchange(ref _handlerFaulted, 0);
            Interlocked.Exchange(ref _responseCompleted, 0);
            _completionSource.Reset();
        }

        public void AppendHeaderBlock(byte[] data, int offset, int length)
        {
            HeaderBlockBuffer.Write(data, offset, length);
        }

        public ArraySegment<byte> GetHeaderBlockSegment()
        {
            if (HeaderBlockBuffer.TryGetBuffer(out var segment))
            {
                return new ArraySegment<byte>(
                    segment.Array,
                    segment.Offset,
                    (int)HeaderBlockBuffer.Length);
            }

            var copy = HeaderBlockBuffer.ToArray();
            return new ArraySegment<byte>(copy, 0, copy.Length);
        }

        public void EnsureResponseBodyCapacity(int capacity)
        {
        }

        public bool HandleResponseHeaders(int statusCode, HttpHeaders headers)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            HeadersReceived = true;
            try
            {
                _handler.OnResponseStart(statusCode, headers ?? new HttpHeaders(), _context);
                return true;
            }
            catch (Exception ex)
            {
                return FailFromHandler(ex);
            }
        }

        public bool AppendResponseData(byte[] source, int offset, int length)
        {
            if (length <= 0)
                return true;

            ThrowIfDisposed();
            try
            {
                _handler.OnResponseData(new ReadOnlySpan<byte>(source, offset, length), _context);
                _responseBodyLength += length;
                return true;
            }
            catch (Exception ex)
            {
                return FailFromHandler(ex);
            }
        }

        public void AppendTrailers(HttpHeaders trailers)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _trailers = trailers;
        }

        public void ClearHeaderBlock()
        {
            HeaderBlockBuffer.SetLength(0);
        }

        public bool Complete()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return false;

            State = Http2StreamState.Closed;
            try
            {
                _handler.OnResponseEnd(_trailers ?? HttpHeaders.Empty, _context);
                TrySetResult();
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _handlerFaulted, 1);
                TrySetException(new HandlerCallbackException(ex));
                return false;
            }
        }

        public void Fail(Exception exception)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            TrySetException(exception ?? new InvalidOperationException("HTTP/2 stream failed."));
        }

        public void Cancel(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            TrySetException(new OperationCanceledException(cancellationToken));
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            CancellationRegistration.Dispose();
            CancellationRegistration = default;
            _handler = TurboHTTP.Transport.NullHandler.Instance;
            _request = null;
            _context = null;
            _trailers = null;
            HeaderBlockBuffer.Dispose();
        }

        void IValueTaskSource.GetResult(short token) => _completionSource.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _completionSource.GetStatus(token);

        void IValueTaskSource.OnCompleted(
            Action<object> continuation,
            object state,
            short token,
            ValueTaskSourceOnCompletedFlags flags) =>
            _completionSource.OnCompleted(continuation, state, token, flags);

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2Stream));
        }

        private bool FailFromHandler(Exception exception)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return false;

            State = Http2StreamState.Closed;
            Interlocked.Exchange(ref _handlerFaulted, 1);
            TrySetException(new HandlerCallbackException(exception));
            return false;
        }

        private void TrySetResult()
        {
            try
            {
                _completionSource.SetResult(default);
            }
            catch (InvalidOperationException)
            {
                // Defensive only: _responseCompleted should already prevent double-set races.
            }
        }

        private void TrySetException(Exception exception)
        {
            try
            {
                _completionSource.SetException(exception);
            }
            catch (InvalidOperationException)
            {
                // Defensive only: _responseCompleted should already prevent double-set races.
            }
        }
    }
}
