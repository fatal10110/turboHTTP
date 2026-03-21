using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using TurboHTTP.Core;
using TurboHTTP.Core.Internal;

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
    /// Represents a single HTTP/2 request/response exchange on a multiplexed connection.
    /// </summary>
    internal sealed class Http2Stream : IDisposable, IValueTaskSource
    {
        private struct VoidResult
        {
        }

        private int _streamId;
        private UHttpRequest _request;
        private RequestContext _context;
        private IHttpHandler _handler;
        private Http2Connection _connection;
        private int _statusCode;
        private HttpHeaders _headers;
        private int _disposed;
        private int _sendWindowSize;
        private int _recvWindowSize;
        private int _responseStarted;
        private int _handlerFaulted;
        private int _completionSignaled;
        private int _lifetimeRefCount;
        private long _responseBodyLength;
        private long? _responseContentLength;
        private Http2ResponseBodySource _responseBodySource;
        private ManualResetValueTaskSourceCore<VoidResult> _completionSource;

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
            Initialize(
                streamId,
                request,
                handler,
                context,
                initialSendWindowSize,
                initialRecvWindowSize,
                null);
        }

        public int StreamId => _streamId;
        public UHttpRequest Request => _request;
        public RequestContext Context => _context;
        public Http2StreamState State { get; set; }
        public long ResponseBodyLength => Interlocked.Read(ref _responseBodyLength);
        public int ResponseBodyCapacity => ResponseBodySource?.BufferCapacity ?? 0;

        /// <summary>
        /// Buffer for accumulating HEADERS + CONTINUATION header blocks.
        /// Allocated once and reused across pool rentals.
        /// </summary>
        public MemoryStream HeaderBlockBuffer { get; }

        public bool HeadersReceived { get; set; }
        public bool PendingEndStream { get; set; }
        public bool ResponseStarted => Volatile.Read(ref _responseStarted) != 0;
        public bool HasHandlerFault => Volatile.Read(ref _handlerFaulted) != 0;
        public bool IsResponseCompleted
        {
            get
            {
                var responseBodySource = Volatile.Read(ref _responseBodySource);
                return Volatile.Read(ref _completionSignaled) != 0 ||
                       (responseBodySource != null && responseBodySource.IsAborted);
            }
        }
        public bool IsStreamingResponseRequested =>
            _context != null &&
            _context.GetState(TransportBehaviorFlags.StreamingResponseRequested, false);
        public Http2ResponseBodySource ResponseBodySource
        {
            get => Volatile.Read(ref _responseBodySource);
            private set => Volatile.Write(ref _responseBodySource, value);
        }
        public CancellationTokenRegistration CancellationRegistration { get; set; }

        public int SendWindowSize
        {
            get => Interlocked.CompareExchange(ref _sendWindowSize, 0, 0);
            set => Interlocked.Exchange(ref _sendWindowSize, value);
        }

        public int RecvWindowSize
        {
            get => Interlocked.CompareExchange(ref _recvWindowSize, 0, 0);
            set => Interlocked.Exchange(ref _recvWindowSize, value);
        }

        public int AdjustSendWindowSize(int delta) => Interlocked.Add(ref _sendWindowSize, delta);

        public int AdjustRecvWindowSize(int delta) => Interlocked.Add(ref _recvWindowSize, delta);

        public ValueTask CompletionTask => new ValueTask(this, _completionSource.Version);

        public void Initialize(
            int streamId,
            UHttpRequest request,
            IHttpHandler handler,
            RequestContext context,
            int initialSendWindowSize,
            int initialRecvWindowSize,
            Http2Connection connection)
        {
            _streamId = streamId;
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _connection = connection;
            _statusCode = 0;
            _headers = null;
            _responseContentLength = null;
            ResponseBodySource = null;
            HeadersReceived = false;
            PendingEndStream = false;
            State = Http2StreamState.Idle;
            HeaderBlockBuffer.SetLength(0);
            _sendWindowSize = initialSendWindowSize;
            _recvWindowSize = initialRecvWindowSize;
            CancellationRegistration = default;
            Interlocked.Exchange(ref _handlerFaulted, 0);
            Interlocked.Exchange(ref _responseStarted, 0);
            Interlocked.Exchange(ref _completionSignaled, 0);
            Interlocked.Exchange(ref _lifetimeRefCount, 1);
            Interlocked.Exchange(ref _responseBodyLength, 0);
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
            _connection = null;
            _statusCode = 0;
            _headers = null;
            _responseContentLength = null;
            ResponseBodySource = null;
            HeadersReceived = false;
            PendingEndStream = false;
            State = Http2StreamState.Idle;
            _sendWindowSize = 0;
            _recvWindowSize = 0;
            Interlocked.Exchange(ref _handlerFaulted, 0);
            Interlocked.Exchange(ref _responseStarted, 0);
            Interlocked.Exchange(ref _completionSignaled, 0);
            Interlocked.Exchange(ref _lifetimeRefCount, 0);
            Interlocked.Exchange(ref _responseBodyLength, 0);
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

        public void ClearHeaderBlock()
        {
            HeaderBlockBuffer.SetLength(0);
        }

        public void AddResponseBodyBytes(int count)
        {
            if (count <= 0)
                return;

            Interlocked.Add(ref _responseBodyLength, count);
        }

        public bool TryStartResponse(
            int statusCode,
            HttpHeaders headers,
            long? contentLength,
            bool endStream)
        {
            ThrowIfDisposed();

            if (ResponseBodySource != null)
                throw new InvalidOperationException("Response body source already created.");

            HeadersReceived = true;
            _statusCode = statusCode;
            _headers = headers ?? new HttpHeaders();
            _responseContentLength = contentLength;
            ResponseBodySource = new Http2ResponseBodySource(_connection, this, contentLength, endStream);
            Interlocked.Exchange(ref _responseStarted, 1);
            Interlocked.Increment(ref _lifetimeRefCount);

            if (IsStreamingResponseRequested)
            {
                CancellationRegistration.Dispose();
                CancellationRegistration = default;
            }

            _ = InvokeResponseStartAsync(ResponseBodySource);
            return true;
        }

        public void AppendTrailers(HttpHeaders trailers)
        {
            ResponseBodySource?.SetTrailers(trailers ?? HttpHeaders.Empty);
        }

        public Http2ResponseBodyEnqueueResult TryAppendResponseData(
            byte[] source,
            int offset,
            int length,
            int flowControlledLength)
        {
            if (length <= 0)
                return Http2ResponseBodyEnqueueResult.Accepted;

            var bodySource = ResponseBodySource;
            if (bodySource == null)
                throw new InvalidOperationException("Response body source is not ready.");

            return bodySource.TryEnqueueData(source, offset, length, flowControlledLength);
        }

        public void CompleteResponseBody()
        {
            ResponseBodySource?.Complete();
        }

        public void FaultResponseBody(Exception error)
        {
            ResponseBodySource?.Fault(error);
        }

        public void ReleaseDispatchLifetime()
        {
            ReleaseLifetime();
        }

        public void ReleaseBodySourceLifetime()
        {
            ReleaseLifetime();
        }

        public bool IsStalled(long nowTick, long stallTimeoutMs)
        {
            var bodySource = ResponseBodySource;
            if (bodySource == null)
                return false;

            return bodySource.IsStalled(nowTick, stallTimeoutMs);
        }

        public void Cancel(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            // Request/transport cancellation keeps its native cancellation shape so
            // streaming consumers can distinguish it from transport/network faults.
            var responseBodySource = ResponseBodySource;
            if (responseBodySource != null)
            {
                responseBodySource.Fault(new OperationCanceledException(cancellationToken));
                return;
            }

            if (Interlocked.Exchange(ref _completionSignaled, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            TrySetException(new OperationCanceledException(cancellationToken));
        }

        public void Fail(Exception exception)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var normalizedException = NormalizeTransportException(
                exception ?? new InvalidOperationException("HTTP/2 stream failed."));

            // Transport/protocol failures stay fault-shaped even after response start so
            // middleware and callers continue to observe them as network errors.
            var responseBodySource = ResponseBodySource;
            if (responseBodySource != null)
            {
                responseBodySource.Fault(normalizedException);
                return;
            }

            if (Interlocked.Exchange(ref _completionSignaled, 1) != 0)
                return;

            State = Http2StreamState.Closed;
            CompleteWithResponseStartFailure(normalizedException);
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
            _headers = null;
            _responseContentLength = null;
            ResponseBodySource = null;
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

        private async Task InvokeResponseStartAsync(Http2ResponseBodySource bodySource)
        {
            try
            {
                await _handler.OnResponseStartAsync(
                        _statusCode,
                        _headers ?? new HttpHeaders(),
                        bodySource,
                        _context)
                    .ConfigureAwait(false);

                TrySetResult();
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _handlerFaulted, 1);
                try
                {
                    bodySource?.Abort();
                }
                catch
                {
                }

                TrySetException(new HandlerCallbackException(ex));
            }
        }

        private void CompleteWithResponseStartFailure(Exception exception)
        {
            try
            {
                _handler.OnResponseError(MapHandlerException(exception), _context);
                TrySetResult();
            }
            catch (Exception errorCallbackException)
            {
                TrySetException(new HandlerCallbackException(errorCallbackException));
            }
        }

        private static UHttpException MapHandlerException(Exception exception)
        {
            if (exception is UHttpException httpException)
                return httpException;

            return new UHttpException(
                new UHttpError(
                    UHttpErrorType.Unknown,
                    exception?.Message ?? "Handler callback failed.",
                    exception));
        }

        private static Exception NormalizeTransportException(Exception exception)
        {
            if (exception == null)
            {
                return new UHttpException(
                    new UHttpError(
                        UHttpErrorType.NetworkError,
                        "HTTP/2 stream failed."));
            }

            if (exception is UHttpException || exception is OperationCanceledException)
                return exception;

            return new UHttpException(
                new UHttpError(
                    UHttpErrorType.NetworkError,
                    exception.Message,
                    exception));
        }

        private void TrySetResult()
        {
            if (Interlocked.Exchange(ref _completionSignaled, 1) != 0)
                return;

            try
            {
                _completionSource.SetResult(default);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void TrySetException(Exception exception)
        {
            if (Interlocked.Exchange(ref _completionSignaled, 1) != 0)
                return;

            try
            {
                _completionSource.SetException(exception);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ReleaseLifetime()
        {
            if (Interlocked.Decrement(ref _lifetimeRefCount) == 0)
                Http2StreamPool.Return(this);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2Stream));
        }
    }
}
