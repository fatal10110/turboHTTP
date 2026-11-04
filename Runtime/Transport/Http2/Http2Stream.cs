using System;
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

        // _closeFlags bits (RFC 9113 Section 5.1). Set with CompareExchange so exactly one side observes
        // the transition to fully closed and removes the stream from the connection's active map.
        private const int LocalClosedFlag = 1;
        private const int RemoteClosedFlag = 2;
        private const int AbandonPendingFlag = 4;

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
        private long _expectedResponseBodyLength = -1;
        private int _closeFlags;
        private int _requestBodyAborted;
        private readonly object _registrationGate = new object();
        private CancellationTokenRegistration _cancellationRegistration;
        private bool _cancellationRegistrationReleased;
        private Http2ResponseBodySource _responseBodySource;
        private CancellationToken _requestCancellationToken;
        private TaskCompletionSource<bool> _expectContinueSource;
        private ManualResetValueTaskSourceCore<VoidResult> _completionSource;

        internal Http2Stream()
        {
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

        public bool HeadersReceived { get; set; }
        public bool ResponseStarted => Volatile.Read(ref _responseStarted) != 0;
        public bool HasHandlerFault => Volatile.Read(ref _handlerFaulted) != 0;
        /// <summary>
        /// True once the request body must not be sent any further: the peer reset the stream, the stream
        /// failed or was cancelled, the handler faulted, the response body was abandoned or faulted. A
        /// response that merely started or completed does NOT stop the upload (RFC 9113 Section 8.1).
        /// </summary>
        public bool IsRequestBodyAborted
        {
            get
            {
                if (Volatile.Read(ref _requestBodyAborted) != 0)
                    return true;

                var responseBodySource = Volatile.Read(ref _responseBodySource);
                return responseBodySource != null && responseBodySource.IsAborted;
            }
        }

        /// <summary>END_STREAM was sent, or the request body was abandoned (RFC 9113 Section 5.1).</summary>
        public bool IsLocalClosed => (Volatile.Read(ref _closeFlags) & LocalClosedFlag) != 0;

        /// <summary>The peer's END_STREAM was received (RFC 9113 Section 5.1).</summary>
        public bool IsRemoteClosed => (Volatile.Read(ref _closeFlags) & RemoteClosedFlag) != 0;

        /// <summary>
        /// The response content-length that the DATA payload must match (RFC 9113 Section 8.1.1), or -1
        /// when there is none or the response has no content by definition (HEAD, 204, 304).
        /// </summary>
        public long ExpectedResponseBodyLength => Interlocked.Read(ref _expectedResponseBodyLength);

        public bool IsStreamingResponseRequested =>
            _context != null &&
            _context.GetState(TransportBehaviorFlags.StreamingResponseRequested, false);
        public Http2ResponseBodySource ResponseBodySource
        {
            get => Volatile.Read(ref _responseBodySource);
            private set => Volatile.Write(ref _responseBodySource, value);
        }

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
            Interlocked.Exchange(ref _expectedResponseBodyLength, -1);
            Interlocked.Exchange(ref _closeFlags, 0);
            Interlocked.Exchange(ref _requestBodyAborted, 0);
            ResponseBodySource = null;
            HeadersReceived = false;
            State = Http2StreamState.Idle;
            _requestCancellationToken = default;
            _sendWindowSize = initialSendWindowSize;
            _recvWindowSize = initialRecvWindowSize;
            _expectContinueSource = null;
            lock (_registrationGate)
            {
                _cancellationRegistration = default;
                _cancellationRegistrationReleased = false;
            }
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
            ReleaseCancellationRegistration();
            lock (_registrationGate)
                _cancellationRegistrationReleased = false;
            _streamId = 0;
            _request = null;
            _context = null;
            _handler = TurboHTTP.Transport.NullHandler.Instance;
            _connection = null;
            _statusCode = 0;
            _headers = null;
            _responseContentLength = null;
            Interlocked.Exchange(ref _expectedResponseBodyLength, -1);
            Interlocked.Exchange(ref _closeFlags, 0);
            Interlocked.Exchange(ref _requestBodyAborted, 0);
            ResponseBodySource = null;
            HeadersReceived = false;
            State = Http2StreamState.Idle;
            _requestCancellationToken = default;
            _sendWindowSize = 0;
            _recvWindowSize = 0;
            TryCancelExpectContinueWait();
            Interlocked.Exchange(ref _handlerFaulted, 0);
            Interlocked.Exchange(ref _responseStarted, 0);
            Interlocked.Exchange(ref _completionSignaled, 0);
            Interlocked.Exchange(ref _lifetimeRefCount, 0);
            Interlocked.Exchange(ref _responseBodyLength, 0);
            _completionSource.Reset();
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
            Interlocked.Exchange(
                ref _expectedResponseBodyLength,
                contentLength.HasValue && !IsNoContentResponse(_request.Method, statusCode) ? contentLength.Value : -1);
            ResponseBodySource = new Http2ResponseBodySource(_connection, this, contentLength, endStream);
            Interlocked.Exchange(ref _responseStarted, 1);
            Interlocked.Increment(ref _lifetimeRefCount);

            // Request cancellation stays active while the upload may still be running (RFC 9113
            // Section 8.1); a streamed body is handed off to its reader once both are done.
            ReleaseCancellationIfStreamingHandedOff();
            _ = InvokeResponseStartAsync(ResponseBodySource);
            return true;
        }

        /// <summary>
        /// RFC 9110 Section 6.4.1: responses to HEAD and 204/304 responses have no content, so their
        /// content-length is not compared with the DATA payload (RFC 9113 Section 8.1.1).
        /// </summary>
        internal static bool IsNoContentResponse(HttpMethod method, int statusCode)
        {
            return method == HttpMethod.HEAD || statusCode == 204 || statusCode == 304 ||
                   (statusCode >= 100 && statusCode < 200);
        }

        /// <summary>Records that END_STREAM was sent. Returns true when this closed both directions.</summary>
        public bool MarkLocalClosed()
        {
            int previous = OrCloseFlags(LocalClosedFlag);
            UpdateStateFromCloseFlags(previous | LocalClosedFlag);
            return (previous & LocalClosedFlag) == 0 && (previous & RemoteClosedFlag) != 0;
        }

        /// <summary>
        /// Abandons the request body after an early final response (RFC 9113 Section 8.1, RFC 9110
        /// Section 10.1.1). The stream is ended with RST_STREAM(NO_ERROR) once the response is complete,
        /// because an earlier reset would also stop the response. Returns true when the response is
        /// already complete, so the caller sends that reset now; otherwise the read loop sends it when
        /// the peer's END_STREAM arrives.
        /// </summary>
        public bool MarkLocalAbandoned()
        {
            AbortRequestBody();
            int previous = OrCloseFlags(LocalClosedFlag | AbandonPendingFlag);
            UpdateStateFromCloseFlags(previous | LocalClosedFlag);
            return (previous & LocalClosedFlag) == 0 && (previous & RemoteClosedFlag) != 0;
        }

        /// <summary>
        /// Records that the peer's END_STREAM was received. Returns true when this closed both
        /// directions; <paramref name="resetNoError"/> is then true when the request body was abandoned
        /// and the stream must be ended with RST_STREAM(NO_ERROR).
        /// </summary>
        public bool MarkRemoteClosed(out bool resetNoError)
        {
            int previous = OrCloseFlags(RemoteClosedFlag);
            UpdateStateFromCloseFlags(previous | RemoteClosedFlag);
            bool closedBoth = (previous & RemoteClosedFlag) == 0 && (previous & LocalClosedFlag) != 0;
            resetNoError = closedBoth && (previous & AbandonPendingFlag) != 0;
            return closedBoth;
        }

        /// <summary>Stops the upload: no DATA or trailers are sent for this stream any more.</summary>
        public void AbortRequestBody()
        {
            if (Interlocked.Exchange(ref _requestBodyAborted, 1) == 0)
                _connection?.WakeSendWindowWaiters();
        }

        /// <summary>
        /// Takes a temporary lifetime reference for code that found this stream through a lookup (the
        /// read loop, connection-wide scans). Fails when the stream was already returned to the pool or
        /// was re-rented for another (connection, stream id), so a recycled object is never mutated.
        /// The caller releases it with <see cref="ReleaseLease"/>.
        /// </summary>
        public bool TryAcquireLease(int expectedStreamId, Http2Connection expectedConnection)
        {
            while (true)
            {
                int current = Volatile.Read(ref _lifetimeRefCount);
                if (current <= 0)
                    return false;

                if (Interlocked.CompareExchange(ref _lifetimeRefCount, current + 1, current) == current)
                    break;
            }

            // Read after the increment: a re-rented stream has its new identity published before its
            // reference count became positive again (Initialize).
            if (Volatile.Read(ref _streamId) == expectedStreamId &&
                ReferenceEquals(Volatile.Read(ref _connection), expectedConnection))
            {
                return true;
            }

            ReleaseLifetime();
            return false;
        }

        public void ReleaseLease()
        {
            ReleaseLifetime();
        }

        /// <summary>
        /// Stores the request-token registration (made once HEADERS are on the wire). Disposed at once
        /// when the registration was already released (a streamed response was handed off first).
        /// </summary>
        public void SetCancellationRegistration(CancellationTokenRegistration registration)
        {
            lock (_registrationGate)
            {
                if (!_cancellationRegistrationReleased)
                {
                    _cancellationRegistration = registration;
                    return;
                }
            }

            registration.Dispose();
        }

        /// <summary>Disposes the request-token registration once; later registrations are disposed at once.</summary>
        public void ReleaseCancellationRegistration()
        {
            CancellationTokenRegistration registration;
            lock (_registrationGate)
            {
                if (_cancellationRegistrationReleased)
                    return;

                _cancellationRegistrationReleased = true;
                registration = _cancellationRegistration;
                _cancellationRegistration = default;
            }

            registration.Dispose();
        }

        /// <summary>
        /// A streamed response belongs to its reader once it has started and the request side is done
        /// (END_STREAM sent or body abandoned): request cancellation no longer aborts it. Called by both
        /// sides (response start, upload end); each publishes its own flag first, so one of them sees both.
        /// </summary>
        public void ReleaseCancellationIfStreamingHandedOff()
        {
            if (IsStreamingResponseRequested && ResponseStarted && IsLocalClosed)
                ReleaseCancellationRegistration();
        }

        public void EnableExpectContinueWait()
        {
            ThrowIfDisposed();

            var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _expectContinueSource, source, null) != null)
                throw new InvalidOperationException("Expect-continue wait is already active.");
        }

        public Task<bool> WaitForExpectContinueAsync()
        {
            var source = Volatile.Read(ref _expectContinueSource);
            return source != null ? source.Task : Task.FromResult(true);
        }

        public void SignalExpectContinueContinue()
        {
            Interlocked.Exchange(ref _expectContinueSource, null)?.TrySetResult(true);
        }

        public void SignalExpectContinueFinalResponse()
        {
            Interlocked.Exchange(ref _expectContinueSource, null)?.TrySetResult(false);
        }

        public void AppendTrailers(HttpHeaders trailers)
        {
            ResponseBodySource?.SetTrailers(trailers ?? HttpHeaders.Empty);
        }

        internal void SetRequestCancellationToken(CancellationToken cancellationToken)
        {
            _requestCancellationToken = cancellationToken;
        }

        internal void HandleRequestCancellation()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _connection?.HandleRequestCancellation(this, _requestCancellationToken);
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
            AbortRequestBody();
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

            AbortRequestBody();
            TryCancelExpectContinueWait(cancellationToken);

            // Request/transport cancellation keeps its native cancellation shape so
            // streaming consumers can distinguish it from transport/network faults.
            var responseBodySource = ResponseBodySource;
            if (responseBodySource != null)
            {
                responseBodySource.Fault(new OperationCanceledException(cancellationToken));
                return;
            }

            State = Http2StreamState.Closed;
            TrySetException(new OperationCanceledException(cancellationToken));
        }

        public void Fail(Exception exception)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            AbortRequestBody();
            var normalizedException = NormalizeTransportException(
                exception ?? new InvalidOperationException("HTTP/2 stream failed."));

            TryFailExpectContinueWait(normalizedException);

            // Transport/protocol failures stay fault-shaped even after response start so
            // middleware and callers continue to observe them as network errors.
            var responseBodySource = ResponseBodySource;
            if (responseBodySource != null)
            {
                responseBodySource.Fault(normalizedException);
                return;
            }

            State = Http2StreamState.Closed;

            // A replayable failure before the response started is not delivered to the handler: the
            // dispatch rethrows it, and RawSocketTransport either replays the request or reports it
            // through OnResponseError itself, so the handler sees exactly one outcome.
            if (normalizedException is Http2RetryableRequestException)
            {
                TrySetException(normalizedException);
                return;
            }

            CompleteWithResponseStartFailure(normalizedException);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            ReleaseCancellationRegistration();
            _handler = TurboHTTP.Transport.NullHandler.Instance;
            _request = null;
            _context = null;
            _headers = null;
            _responseContentLength = null;
            ResponseBodySource = null;
            TryCancelExpectContinueWait();
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
                AbortRequestBody();
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

            // IsResponseCompleted just turned true: an uploader blocked on the send window stops now
            // (RFC 9113 Section 8.1: e.g. an early 413 while the request body is still flowing).
            _connection?.WakeSendWindowWaiters();

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

            _connection?.WakeSendWindowWaiters();

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
            if (Interlocked.Decrement(ref _lifetimeRefCount) != 0)
                return;

            // Never leave a pooled object reachable through the connection's active map.
            _connection?.RemoveActiveStream(_streamId, this);
            Http2StreamPool.Return(this);
        }

        private int OrCloseFlags(int flags)
        {
            while (true)
            {
                int current = Volatile.Read(ref _closeFlags);
                if (Interlocked.CompareExchange(ref _closeFlags, current | flags, current) == current)
                    return current;
            }
        }

        private void UpdateStateFromCloseFlags(int flags)
        {
            bool local = (flags & LocalClosedFlag) != 0;
            bool remote = (flags & RemoteClosedFlag) != 0;
            State = local && remote
                ? Http2StreamState.Closed
                : local
                    ? Http2StreamState.HalfClosedLocal
                    : remote ? Http2StreamState.HalfClosedRemote : Http2StreamState.Open;
        }

        private void TryFailExpectContinueWait(Exception exception)
        {
            var source = Interlocked.Exchange(ref _expectContinueSource, null);
            source?.TrySetException(exception ?? new InvalidOperationException("HTTP/2 expect-continue wait failed."));
        }

        private void TryCancelExpectContinueWait(CancellationToken cancellationToken = default)
        {
            var source = Interlocked.Exchange(ref _expectContinueSource, null);
            if (source == null)
                return;

            if (cancellationToken.CanBeCanceled)
            {
                source.TrySetCanceled(cancellationToken);
            }
            else
            {
                source.TrySetCanceled();
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(Http2Stream));
        }
    }
}
