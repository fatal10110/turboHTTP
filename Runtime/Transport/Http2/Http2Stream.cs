using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
    internal class Http2Stream : IDisposable
    {
        // Backing fields for per-request identity. Set by Initialize(), cleared by PrepareForPool().
        private int _streamId;
        private UHttpRequest _request;
        private RequestContext _context;

        public int StreamId => _streamId;
        public UHttpRequest Request => _request;
        public RequestContext Context => _context;

        public Http2StreamState State { get; set; }

        public int StatusCode { get; set; }
        public HttpHeaders ResponseHeaders { get; set; }
        public int ResponseBodyLength => _responseBodyLength;
        public int ResponseBodyCapacity => _responseBodyBuffer?.Length ?? 0;

        /// <summary>
        /// Buffer for accumulating HEADERS + CONTINUATION header blocks.
        /// Allocated once in the constructor; reset via <see cref="MemoryStream.SetLength"/>
        /// between pool uses rather than disposed and reallocated.
        /// </summary>
        public MemoryStream HeaderBlockBuffer { get; }

        public bool HeadersReceived { get; set; }
        public bool PendingEndStream { get; set; }

        private byte[] _responseBodyBuffer;
        private int _responseBodyLength;
        private int _disposed;
        private int _responseCompleted;
        private PoolableValueTaskSource<UHttpResponse> _responseSource;
        private ValueTask<UHttpResponse> _responseTask;

        private int _sendWindowSize;
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

        public ValueTask<UHttpResponse> ResponseTask => _responseTask;
        public bool IsResponseCompleted => Volatile.Read(ref _responseCompleted) != 0;

        public CancellationTokenRegistration CancellationRegistration { get; set; }

        // ── Construction ─────────────────────────────────────────────────────────

        /// <summary>
        /// Parameterless constructor for the pool factory. Allocates the long-lived
        /// <see cref="HeaderBlockBuffer"/> once; per-request state is set by
        /// <see cref="Initialize"/>.
        /// </summary>
        internal Http2Stream()
        {
            HeaderBlockBuffer = new MemoryStream();
        }

        /// <summary>
        /// Single-step constructor — allocates the MemoryStream then immediately
        /// initialises per-request state. Used in unit tests and outside pool paths.
        /// </summary>
        public Http2Stream(
            int streamId,
            UHttpRequest request,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize,
            PoolableValueTaskSourcePool<UHttpResponse> responseSourcePool)
        {
            HeaderBlockBuffer = new MemoryStream();
            Initialize(streamId, request, context,
                initialSendWindowSize, initialRecvWindowSize, responseSourcePool);
        }

        // ── Pool lifecycle ───────────────────────────────────────────────────────

        /// <summary>
        /// Sets all per-request state on a pooled (or freshly created) instance.
        /// Called by <see cref="Http2StreamPool.Rent"/> immediately after renting.
        /// </summary>
        public void Initialize(
            int streamId,
            UHttpRequest request,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize,
            PoolableValueTaskSourcePool<UHttpResponse> responseSourcePool)
        {
            _streamId = streamId;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _context = context ?? throw new ArgumentNullException(nameof(context));

            State = Http2StreamState.Idle;
            StatusCode = 0;
            ResponseHeaders = null;

            // Reset MemoryStream position without releasing its backing buffer.
            HeaderBlockBuffer.SetLength(0);
            HeadersReceived = false;
            PendingEndStream = false;

            _sendWindowSize = initialSendWindowSize;
            RecvWindowSize = initialRecvWindowSize;

            _responseBodyBuffer = null;
            _responseBodyLength = 0;
            _disposed = 0;
            _responseCompleted = 0;

            _responseSource = (responseSourcePool ?? throw new ArgumentNullException(nameof(responseSourcePool))).Rent();
            _responseTask = _responseSource.CreateValueTask();

            CancellationRegistration = default;
        }

        /// <summary>
        /// Clears all per-request state and returns pooled resources so this instance
        /// can be stored in <see cref="Http2StreamPool"/> and reused. Called by the
        /// pool's reset delegate via <see cref="Http2StreamPool.Return"/>.
        /// </summary>
        /// <remarks>
        /// Does NOT dispose <see cref="HeaderBlockBuffer"/>; it is intentionally kept
        /// alive for reuse. When the pool is full and discards a stream the MemoryStream
        /// is GC'd — safe because MemoryStream holds no finalizable native resources.
        /// </remarks>
        public void PrepareForPool()
        {
            // Return any partial response body buffer not already consumed by Complete().
            if (_responseBodyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_responseBodyBuffer);
                _responseBodyBuffer = null;
            }
            _responseBodyLength = 0;

            // Deregister the per-request cancellation callback.
            CancellationRegistration.Dispose();
            CancellationRegistration = default;

            // Reset MemoryStream to empty without disposing it (reuse for next request).
            HeaderBlockBuffer.SetLength(0);

            // Clear all per-request references to prevent retention after pool return.
            _streamId = 0;
            _request = null;
            _context = null;
            ResponseHeaders = null;
            State = Http2StreamState.Idle;
            StatusCode = 0;
            HeadersReceived = false;
            PendingEndStream = false;
            _sendWindowSize = 0;
            RecvWindowSize = 0;
            _responseSource = null;  // Already returned to its own pool by the caller.
            // Use Volatile.Write to establish a release fence on ARM — ensures all preceding
            // field clears are visible to any thread that subsequently observes _disposed == 0.
            Volatile.Write(ref _disposed, 0);
            Volatile.Write(ref _responseCompleted, 0);
        }

        // ── Public API ───────────────────────────────────────────────────────────

        public void AppendHeaderBlock(byte[] data, int offset, int length)
        {
            // Use MemoryStream.Write for efficient bulk copy instead of byte-by-byte
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

            // Fallback for non-exposable buffers.
            var copy = HeaderBlockBuffer.ToArray();
            return new ArraySegment<byte>(copy, 0, copy.Length);
        }

        public void EnsureResponseBodyCapacity(int capacity)
        {
            if (capacity <= 0)
                return;

            EnsureBodyCapacity(capacity);
        }

        public void AppendResponseData(byte[] source, int offset, int length)
        {
            if (length <= 0)
                return;

            ThrowIfDisposed();
            EnsureBodyCapacity(_responseBodyLength + length);
            Buffer.BlockCopy(source, offset, _responseBodyBuffer, _responseBodyLength, length);
            _responseBodyLength += length;
        }

        public void ClearHeaderBlock()
        {
            HeaderBlockBuffer.SetLength(0);
        }

        public void Complete()
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;

            var bodyBuffer = _responseBodyBuffer;
            var bodyLength = _responseBodyLength;
            var bodyFromPool = bodyBuffer != null && bodyLength > 0;

            // Transfer ownership of the pooled body to UHttpResponse.
            _responseBodyBuffer = null;
            _responseBodyLength = 0;

            var response = new UHttpResponse(
                statusCode: (HttpStatusCode)StatusCode,
                headers: ResponseHeaders ?? new HttpHeaders(),
                body: bodyFromPool
                    ? new ReadOnlyMemory<byte>(bodyBuffer, 0, bodyLength)
                    : ReadOnlyMemory<byte>.Empty,
                elapsedTime: Context.Elapsed,
                request: Request,
                error: null,
                bodyFromPool: bodyFromPool
            );

            _responseSource.SetResult(response);
        }

        public void Fail(Exception exception)
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            _responseSource.SetException(exception ?? new InvalidOperationException("HTTP/2 stream failed."));
        }

        public void Cancel(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _responseCompleted, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            _responseSource.SetCanceled(cancellationToken);
        }

        public void CancelWithoutConsumption(CancellationToken cancellationToken = default)
        {
            Cancel(cancellationToken);
            _responseSource.ReturnWithoutConsumption();
        }

        public void ReturnResponseSourceIfUnconsumed()
        {
            _responseSource.ReturnWithoutConsumption();
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            CancellationRegistration.Dispose();
            if (_responseBodyBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(_responseBodyBuffer);
                _responseBodyBuffer = null;
                _responseBodyLength = 0;
            }
            HeaderBlockBuffer?.Dispose();
        }

        private void EnsureBodyCapacity(int required)
        {
            ThrowIfDisposed();
            if (required <= 0)
                return;

            if (_responseBodyBuffer == null)
            {
                _responseBodyBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(1024, required));
                return;
            }

            if (_responseBodyBuffer.Length >= required)
                return;

            var resized = ArrayPool<byte>.Shared.Rent(Math.Max(required, _responseBodyBuffer.Length * 2));
            try
            {
                if (_responseBodyLength > 0)
                    Buffer.BlockCopy(_responseBodyBuffer, 0, resized, 0, _responseBodyLength);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(resized);
                throw;
            }

            ArrayPool<byte>.Shared.Return(_responseBodyBuffer);
            _responseBodyBuffer = resized;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2Stream));
        }
    }
}
